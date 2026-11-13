/*
 * 文件职责与维护说明：
 * 生产趋势的查询与图表协调器：按成功查询结果更新白班/夜班堆叠图、记录天数与汇总。
 * 切换来源、离页或新查询使旧结果失效；从趋势打开日报时携带对应日期及已应用来源。
 */
using Company.Application.Main.Models;
using Company.Application.Main.Presentation;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Capacity;
using Company.Application.Share.PLC.Capacity.Models;
using System.Windows.Threading;
using Company.Application.Share.Prism;
using Company.Core.Ioc;
using Company.Database.PLC.Models;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Company.Application.Main.Services.Diagnostics;
namespace Company.Application.Main.ViewModels;

public sealed class ProductionTrendViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    private readonly IProductionCapacityQueryService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _visible, _busy, _hasQuery;
    private bool _cacheUpdatesPaused;
    private string _status = "请输入设备编号并选择日期，点击查询。", _range = "尚未查询", _sync = "", _day = "—", _night = "—", _total = "—", _days = "—", _scale = "—";
    private ProductionTrendPoint? _selected;
    private ProductionTrendQuery? _applied;
    private ProductionCapacityDataSource _appliedSource;
    private ProductionCapacityDataSource _calendarSource;
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private int _cacheRefreshQueued;
    public ProductionQueryFilter Filter { get; } = new();
    public bool KeepAlive => true;
    public bool IsBusy { get => _busy; private set { this.RaiseAndSetIfChanged(ref _busy, value); NotifyCommands(); } }
    public bool CanQuery => _visible && !IsBusy;
    public bool CanCancel => _visible && _request is not null;
    public bool HasQuery { get => _hasQuery; private set { this.RaiseAndSetIfChanged(ref _hasQuery, value); NotifyCommands(); } }
    public bool CanOpenDaily => CanQuery && HasQuery && SelectedItem is not null && _applied is not null;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public string Range { get => _range; private set => this.RaiseAndSetIfChanged(ref _range, value); }
    public string Synchronization { get => _sync; private set => this.RaiseAndSetIfChanged(ref _sync, value); }
    public string Day { get => _day; private set => this.RaiseAndSetIfChanged(ref _day, value); }
    public string Night { get => _night; private set => this.RaiseAndSetIfChanged(ref _night, value); }
    public string Total { get => _total; private set => this.RaiseAndSetIfChanged(ref _total, value); }
    public string Days { get => _days; private set => this.RaiseAndSetIfChanged(ref _days, value); }
    public string Scale { get => _scale; private set => this.RaiseAndSetIfChanged(ref _scale, value); }
    public ObservableCollection<ProductionTrendPoint> Items { get; } = [];
    public ProductionTrendPoint? SelectedItem { get => _selected; set { this.RaiseAndSetIfChanged(ref _selected, value); NotifyCommands(); this.RaisePropertyChanged(nameof(SelectedDescription)); } }
    public string SelectedDescription => SelectedItem?.Description ?? "选择日期柱形或表格行可查看当日状态。";
    public string CalendarNote => ProductionCapacityCalendar.Description(_calendarSource) +
        "累计只包含可确认的已结束小时；无记录显示 —，有记录且为零显示 0。缺口不推算产量。";
    public ICommand QueryCommand { get; }
    public ICommand SelectPointCommand { get; }
    public ICommand OpenDailyCommand { get; }
    public ICommand CancelCommand { get; }
    public ProductionTrendViewModel(IProductionCapacityQueryService service)
    {
        _service = service; Filter.DeviceId = service.DefaultDeviceId; Filter.DataSource = ProductionCapacityDataSource.DatabaseHistory;
        SetCalendarSource(Filter.DataSource, Filter.SourceId);
        Filter.PropertyChanged += (_, e) => {
            if ((e.PropertyName == nameof(ProductionQueryFilter.DataSource) || e.PropertyName == nameof(ProductionQueryFilter.HistoryChoice)) && !HasQuery && !IsBusy) SetCalendarSource(Filter.DataSource, Filter.SourceId);
        };
        var scheduler = new SynchronizationContextScheduler(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        QueryCommand = ReactiveCommand.CreateFromTask(QueryAsync, outputScheduler: scheduler);
        CancelCommand = ReactiveCommand.Create(CancelQuery, outputScheduler: scheduler);
        SelectPointCommand = ReactiveCommand.Create<ProductionTrendPoint>(p => SelectedItem = p, outputScheduler: scheduler);
        OpenDailyCommand = ReactiveCommand.Create(() => {
            if (!CanOpenDaily || BuildDailyQuery() is not { } query) return;
            PrismProvider.RegionManager.RequestNavigate(RegionNames.WorkspaceRegion, ViewNames.ProductionDailyView,
                result => { if (result.Exception is not null) Status = "日报页面打开失败，请查看日志。"; },
                new NavigationParameters { { "ProductionQuery", query }, { "ProductionSource", _appliedSource } });
        }, outputScheduler: scheduler);
    }
    private void NotifyCommands() { this.RaisePropertyChanged(nameof(CanQuery)); this.RaisePropertyChanged(nameof(CanCancel)); this.RaisePropertyChanged(nameof(CanOpenDaily)); }
    /// <summary>取消的是本页等待与缓存刷新，原后台查询自行收尾，不改变采集或产能同步。</summary>
    public void CancelQuery()
    {
        if (!CanCancel) return;
        _cacheUpdatesPaused = true;
        var request = _request; _request = null; ++_generation; request?.Cancel(); IsBusy = false;
        Status = "查询已取消；保留上次结果。再次查询或重新打开页面可恢复刷新。";
    }
    // 趋势跳转日报取自最后成功的查询和所选日期，避免使用尚未提交的新筛选条件。
    public ProductionDayQuery? BuildDailyQuery() => _applied is { } q && SelectedItem is { } p ?
        new(q.DeviceId, p.Date, [q.CounterIndex]) { SourceId = q.SourceId } : null;
    public Task QueryAsync()
    {
        if (!_visible) return Task.CompletedTask;
        _cacheUpdatesPaused = false;
        try { return RunQueryAsync(Filter.Trend(), Filter.DataSource, false); }
        catch (ArgumentException ex) { Status = ex.Message; return Task.CompletedTask; }
    }
    // 串行查询后只允许最新代次更新图形。汇总只包含有记录的数量，全部缺失时保持空值。
    // 白班和夜班按同一最大值转换图形高度，不把图形尺度当作新的产量统计。
    private async Task RunQueryAsync(ProductionTrendQuery query, ProductionCapacityDataSource source, bool cacheUpdate)
    {
        _request?.Cancel(); var request = new CancellationTokenSource(); _request = request; long generation = ++_generation;
        var token = request.Token; long startedAt = Stopwatch.GetTimestamp();
        IsBusy = true;
        bool sameQuery = _applied is { } applied && applied.DeviceId == query.DeviceId && applied.FromDate == query.FromDate &&
            applied.ToDate == query.ToDate && applied.CounterIndex == query.CounterIndex && applied.SourceId == query.SourceId && _appliedSource == source;
        var selectedDate = sameQuery ? SelectedItem?.Date : null;
        // 新结果返回前保留已展示的图表、查询范围和选中行。
        // 来源口径和统计值随成功结果一并更新，避免中间空白帧。
        if (!cacheUpdate || !sameQuery) Status = "正在查询" + ProductionDisplayFormatter.Source(source) + "…";
        try
        {
            var pending = Task.Run(async () => {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try { return await _service.QueryTrendAsync(query, source, token).ConfigureAwait(false); } finally { _gate.Release(); }
            }, token);
            var result = await QueryDiagnostics.AwaitAsync(pending, token);
            if (!_visible || generation != _generation || request.IsCancellationRequested) return;
            _applied = query; _appliedSource = source;
            SetCalendarSource(source, query.SourceId);
            decimal? Sum(Func<ProductionTrendDay, decimal?> value) {
                var values = result.Days.Select(value).Where(n => n.HasValue).ToArray();
                return values.Length == 0 ? null : values.Sum(n => n!.Value);
            }
            Day = ProductionDisplayFormatter.Quantity(Sum(d => d.DayQuantity)); Night = ProductionDisplayFormatter.Quantity(Sum(d => d.NightQuantity));
            Total = ProductionDisplayFormatter.Quantity(Sum(d => d.RecordedTotal));
            Days = result.Days.Count(d => d.RecordedTotal.HasValue) + " / " + result.Days.Count;
            decimal max = result.Days.Select(d => (d.DayQuantity ?? 0) + (d.NightQuantity ?? 0)).DefaultIfEmpty(0).Max();
            Scale = "柱形满刻度 " + ProductionDisplayFormatter.Quantity(max) + " 件；白班／夜班堆叠绘制";
            double Height(decimal? value) => max == 0 || value is null ? 0 : (double)(value.Value / max * 210);
            ProductionTrendPoint.Synchronize(Items, result.Days.Select(d => new ProductionTrendPoint(d, Height(d.DayQuantity), Height(d.NightQuantity), ProductionDisplayFormatter.Source(source))).ToArray());
            SelectedItem = Items.FirstOrDefault(p => p.Date == selectedDate);
            Range = $"设备 {query.DeviceId} · 提升机 · {ProductionDisplayFormatter.Source(source)} · 生产日期 {query.FromDate:yyyy-MM-dd} 至 {query.ToDate:yyyy-MM-dd}（含）";
            Synchronization = result.SourceStatus + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
            Status = result.Status;
            HasQuery = true;
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "查询已取消。"; }
        catch (Exception ex) { if (generation == _generation) { var id = QueryDiagnostics.Failure("产量趋势", QueryDiagnostics.Trend(query, source), startedAt, ex); Status = "趋势查询失败，错误编号：" + id + (HasQuery ? "。保留上次结果，范围以原查询说明为准。" : "。失败不作为零产量。"); } }
        finally { if (generation == _generation) IsBusy = false; if (ReferenceEquals(_request, request)) _request = null; request.Dispose(); this.RaisePropertyChanged(nameof(CanCancel)); }
    }
    // 缓存通知来自后台，先合并并投递到 UI 后台优先级，减少密集通知阻塞导航。
    private void CacheChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cacheRefreshQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(new Action(async () => {
            Interlocked.Exchange(ref _cacheRefreshQueued, 0);
            if (_visible && !_cacheUpdatesPaused && !IsBusy && _applied is { } query && _appliedSource == ProductionCapacityDataSource.PlcCache)
                await RunQueryAsync(query, _appliedSource, true);
        }), DispatcherPriority.Background);
    }
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return; _visible = visible;
        if (visible)
        {
            _cacheUpdatesPaused = false;
            _service.CacheChanged += CacheChanged;
            if (string.IsNullOrWhiteSpace(Filter.DeviceId)) Filter.DeviceId = _service.DefaultDeviceId;
            if (_applied is not null && _appliedSource == ProductionCapacityDataSource.PlcCache) CacheChanged(this, EventArgs.Empty);
        }
        else { _service.CacheChanged -= CacheChanged; ++_generation; _request?.Cancel(); IsBusy = false; Status = "页面已离开，再次查询可刷新统计。"; }
        NotifyCommands();
    }
    public void OnNavigatedTo(NavigationContext context) => SetVisible(true);
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) => SetVisible(false);
    private void SetCalendarSource(ProductionCapacityDataSource source, string? historySource)
    {
        var next = ProductionCapacityCalendar.CalendarFor(source, historySource);
        if (_calendarSource == next) return;
        _calendarSource = next; this.RaisePropertyChanged(nameof(CalendarNote));
    }
}


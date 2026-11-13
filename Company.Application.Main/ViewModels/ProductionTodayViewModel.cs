/*
 * 文件职责与维护说明：
 * 今日产量与日报共用的页面查询行为，分别处理 PLC 缓存和历史来源。
 * 异步期间保留已成功展示的结果；两个班次分别汇总小时数据，PLC 班次累计只作核对，不重复加入总量。
 */
using Company.Application.Main.Models;
using Company.Application.Main.Presentation;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Capacity;
using Company.Application.Share.PLC.Capacity.Models;
using Company.Database.PLC.Models;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Company.Application.Main.Services.Diagnostics;

namespace Company.Application.Main.ViewModels;

public sealed class ProductionTodayViewModel(IProductionCapacityQueryService service) : ProductionDayViewModelBase(service, true);

/// <summary>仅为今日产量与日报复用的页面行为，不属于框架基类。</summary>
public abstract class ProductionDayViewModelBase : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    private readonly IProductionCapacityQueryService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private readonly bool _today;
    private CancellationTokenSource? _request;
    private long _generation;
    private int _cacheRefreshQueued;
    private bool _visible, _busy, _hasQuery;
    private bool _cacheUpdatesPaused;
    private ProductionDayQuery? _applied;
    private ProductionCapacityDataSource _appliedSource;
    private ProductionCapacityDataSource _calendarSource;
    private string _status = "请选择来源并点击查询／刷新。", _range = "尚未查询", _sync = "", _warnings = "";
    private string _notice = "";
    private string _first = "—", _second = "—", _third = "—";
    private string _dayState = "尚未查询", _nightState = "尚未查询", _dayTotal = "—", _nightTotal = "—";
    public ProductionQueryFilter Filter { get; } = new();
    public bool KeepAlive => true;
    public string Title => _today ? "今日产量" : "生产日报";
    public string ActionText => _today ? "刷新" : "查询";
    public bool IsToday => _today;
    public bool IsBusy { get => _busy; private set { this.RaiseAndSetIfChanged(ref _busy, value); this.RaisePropertyChanged(nameof(CanQuery)); this.RaisePropertyChanged(nameof(CanCancel)); } }
    public bool CanQuery => _visible && !IsBusy;
    public bool CanCancel => _visible && _request is not null;
    public bool HasQuery { get => _hasQuery; private set => this.RaiseAndSetIfChanged(ref _hasQuery, value); }
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public string Notice { get => _notice; private set => this.RaiseAndSetIfChanged(ref _notice, value); }
    public string Range { get => _range; private set => this.RaiseAndSetIfChanged(ref _range, value); }
    public string Synchronization { get => _sync; private set => this.RaiseAndSetIfChanged(ref _sync, value); }
    public string Warnings { get => _warnings; private set => this.RaiseAndSetIfChanged(ref _warnings, value); }
    public string First { get => _first; private set => this.RaiseAndSetIfChanged(ref _first, value); }
    public string Second { get => _second; private set => this.RaiseAndSetIfChanged(ref _second, value); }
    public string Third { get => _third; private set => this.RaiseAndSetIfChanged(ref _third, value); }
    public string FirstLabel => _today ? "提升机 · 今日合计" : "提升机 · 所选日合计";
    public string SecondLabel => "提升机 · 白班累计";
    public string ThirdLabel => "提升机 · 夜班累计";
    public string DayHeader => ProductionCapacityCalendar.DayHeader(_calendarSource);
    public string NightHeader => ProductionCapacityCalendar.NightHeader(_calendarSource);
    public string CalendarNote => ProductionCapacityCalendar.Description(_calendarSource) +
        "PLC缓存的白班、夜班均累计已完结及进行中小时；待开始显示 /，不计入合计。历史来源只统计已入库区间。PLC班次累计仅供核对，不重复相加。";
    public string DayState { get => _dayState; private set => this.RaiseAndSetIfChanged(ref _dayState, value); }
    public string NightState { get => _nightState; private set => this.RaiseAndSetIfChanged(ref _nightState, value); }
    public string DayTotal { get => _dayTotal; private set => this.RaiseAndSetIfChanged(ref _dayTotal, value); }
    public string NightTotal { get => _nightTotal; private set => this.RaiseAndSetIfChanged(ref _nightTotal, value); }
    public ObservableCollection<ProductionPeriodRow> DayRows { get; } = [];
    public ObservableCollection<ProductionPeriodRow> NightRows { get; } = [];
    public ICommand QueryCommand { get; }
    public ICommand CancelCommand { get; }

    protected ProductionDayViewModelBase(IProductionCapacityQueryService service, bool today)
    {
        _service = service; _today = today;
        Filter.DataSource = today ? ProductionCapacityDataSource.PlcCache : ProductionCapacityDataSource.DatabaseHistory;
        Filter.DeviceId = service.DefaultDeviceId;
        SetCalendarSource(Filter.DataSource, Filter.SourceId);
        Filter.PropertyChanged += (_, e) => {
            if ((e.PropertyName == nameof(ProductionQueryFilter.DataSource) || e.PropertyName == nameof(ProductionQueryFilter.HistoryChoice)) && !HasQuery && !IsBusy) SetCalendarSource(Filter.DataSource, Filter.SourceId);
        };
        var scheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(_dispatcher));
        QueryCommand = ReactiveCommand.CreateFromTask(QueryAsync, outputScheduler: scheduler);
        CancelCommand = ReactiveCommand.Create(CancelQuery, outputScheduler: scheduler);
    }
    /// <summary>只取消页面查询等待，不停止产能后台采集或同步。暂停本页缓存驱动刷新，避免取消后立即重启。</summary>
    public void CancelQuery()
    {
        if (!CanCancel) return;
        _cacheUpdatesPaused = true;
        var request = _request; _request = null; ++_generation; request?.Cancel(); IsBusy = false;
        Notice = Status = "查询已取消；保留上次结果。再次查询或重新打开页面可恢复刷新。";
    }
    public Task QueryAsync()
    {
        if (!_visible) return Task.CompletedTask;
        _cacheUpdatesPaused = false;
        try
        {
            var query = Filter.Day(_today ? ProductionCapacityCalendar.DateFor(Filter.DataSource, DateTime.UtcNow, Filter.SourceId) : null);
            if (_today) Filter.Date = query.ProductionDate;
            return RunQueryAsync(query, Filter.DataSource, false);
        }
        catch (ArgumentException ex) { Notice = Status = ex.Message; return Task.CompletedTask; }
    }
    // 查询开始记录来源、设备和生产日；结果返回后再检查可见性、取消与查询代次。
    // 新结果成功前保留原展示范围及行，不能让查询失败呈现为当日产量归零。
    private async Task RunQueryAsync(ProductionDayQuery query, ProductionCapacityDataSource source, bool cacheUpdate)
    {
        _request?.Cancel(); var request = new CancellationTokenSource(); _request = request; long generation = ++_generation;
        var token = request.Token; long startedAt = Stopwatch.GetTimestamp();
        IsBusy = true;
        Notice = "";
        bool sameQuery = _applied is { } applied && applied.DeviceId == query.DeviceId &&
            applied.ProductionDate == query.ProductionDate && applied.SourceId == query.SourceId && _appliedSource == source;
        if (!sameQuery)
        {
            Status = "正在读取" + ProductionDisplayFormatter.Source(source) + (HasQuery ? "…（保留上次结果，范围以原查询说明为准）" : "…");
        }
        try
        {
            var pending = Task.Run(async () => {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try { return await _service.QueryDayAsync(query, source, token).ConfigureAwait(false); } finally { _gate.Release(); }
            }, token);
            var result = await QueryDiagnostics.AwaitAsync(pending, token);
            if (!_visible || generation != _generation || request.IsCancellationRequested) return;
            _applied = query; _appliedSource = source;
            SetCalendarSource(source, query.SourceId);
            var day = result.Shifts.Single(s => s.Code == "Day"); var night = result.Shifts.Single(s => s.Code == "Night");
            First = ProductionDisplayFormatter.Quantity(day.Quantity is null && night.Quantity is null ? null : (day.Quantity ?? 0) + (night.Quantity ?? 0));
            Second = ProductionDisplayFormatter.Quantity(day.Quantity); Third = ProductionDisplayFormatter.Quantity(night.Quantity);
            ProductionPeriodRow.Synchronize(DayRows, ProductionPeriodRow.Build(result, 0));
            ProductionPeriodRow.Synchronize(NightRows, ProductionPeriodRow.Build(result, 1));
            DayState = day.Status; NightState = night.Status;
            string Summary(ProductionCapacityShiftResult shift) => "班次小时合计 " + ProductionDisplayFormatter.Quantity(shift.Quantity) + " 件" +
                (shift.PlcQuantity is { } plc ? " · PLC班次累计 " + ProductionDisplayFormatter.Quantity(plc) + " 件（不相加）" : "");
            DayTotal = Summary(day); NightTotal = Summary(night);
            Range = $"设备 {query.DeviceId} · 提升机 · {ProductionDisplayFormatter.Source(source)} · 生产日 {query.ProductionDate:yyyy-MM-dd} · " +
                ProductionDisplayFormatter.Time(day.StartUtc, source, query.SourceId) + " 至 " + ProductionDisplayFormatter.Time(night.EndUtc, source, query.SourceId) +
                (ProductionCapacityCalendar.CalendarFor(source, query.SourceId) == ProductionCapacityDataSource.PlcCache ? "（北京时间，结束不含）" : "（原历史口径，结束不含）");
            Synchronization = result.SourceStatus; Warnings = string.Join("\n", result.Warnings);
            // 名称乱码已通过有效班次时间窗口兼容处理；完整诊断保留在 Warnings，不占用操作页面。
            Notice = string.Join("\n", result.Warnings.Where(w => !IsShiftNameFallback(w)));
            Status = result.Status; HasQuery = true;
        }
        catch (OperationCanceledException) { if (generation == _generation) Notice = Status = "查询已取消。"; }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                if (HasQuery) DayState = NightState = "刷新失败，以下保留上次结果";
                var id = QueryDiagnostics.Failure(Title, QueryDiagnostics.Day(query, source), startedAt, ex);
                Status = "产量查询失败，错误编号：" + id + (HasQuery ? "。保留上次结果，范围以原查询说明为准。" : "。失败不作为零产量结果。");
                Notice = Status;
            }
        }
        finally { if (generation == _generation) IsBusy = false; if (ReferenceEquals(_request, request)) _request = null; request.Dispose(); this.RaisePropertyChanged(nameof(CanCancel)); }
    }
    // 后台缓存变化只合并排队一次 UI 更新；隐藏页或忙状态下不抢占当前查询。
    // 更新沿用上次已应用的来源与查询，不擅自改变用户选择。
    private void CacheChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cacheRefreshQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(new Action(async () => {
            Interlocked.Exchange(ref _cacheRefreshQueued, 0);
            if (_visible && !_cacheUpdatesPaused && !IsBusy && _applied is { } query && _appliedSource == ProductionCapacityDataSource.PlcCache)
            {
                var applied = _today ? query with { ProductionDate = ProductionCapacityCalendar.CurrentProductionDate(DateTime.UtcNow) } : query;
                if (_today) Filter.Date = applied.ProductionDate;
                await RunQueryAsync(applied, _appliedSource, true);
            }
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
            if (_today && _applied is null && Filter.DeviceId.Length > 0 && Filter.DataSource == ProductionCapacityDataSource.PlcCache) _ = QueryAsync();
            else if (_appliedSource == ProductionCapacityDataSource.PlcCache && _applied is not null) CacheChanged(this, EventArgs.Empty);
        }
        else { _service.CacheChanged -= CacheChanged; ++_generation; _request?.Cancel(); IsBusy = false; }
        this.RaisePropertyChanged(nameof(CanQuery));
        this.RaisePropertyChanged(nameof(CanCancel));
    }
    public void OnNavigatedTo(NavigationContext context)
    {
        if (!_today && context.Parameters.TryGetValue<ProductionDayQuery>("ProductionQuery", out var query))
        {
            try
            {
                Filter.Apply(query);
                if (context.Parameters.TryGetValue<ProductionCapacityDataSource>("ProductionSource", out var source)) Filter.DataSource = source;
                ++_generation; _request?.Cancel(); IsBusy = false;
                HasQuery = false; _applied = null;
                Status = "已带入设备、日期和来源，请点击查询；列表保留上次结果，范围以原查询说明为准。"; Notice = "";
            }
            catch (ArgumentException ex) { Notice = Status = ex.Message; }
        }
        SetVisible(true);
    }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) => SetVisible(false);
    private void SetCalendarSource(ProductionCapacityDataSource source, string? historySource)
    {
        var next = ProductionCapacityCalendar.CalendarFor(source, historySource);
        if (_calendarSource == next) return;
        _calendarSource = next;
        this.RaisePropertyChanged(nameof(DayHeader)); this.RaisePropertyChanged(nameof(NightHeader)); this.RaisePropertyChanged(nameof(CalendarNote));
    }
    private void ClearRows()
    {
        ProductionPeriodRow.Synchronize(DayRows, []);
        ProductionPeriodRow.Synchronize(NightRows, []);
    }
    private static bool IsShiftNameFallback(string warning) =>
        warning.StartsWith("班次槽", StringComparison.Ordinal) &&
        warning.Contains("名称无法识别，原始文本（转义）", StringComparison.Ordinal) &&
        warning.Contains("；按有效时间窗口显示", StringComparison.Ordinal);
}

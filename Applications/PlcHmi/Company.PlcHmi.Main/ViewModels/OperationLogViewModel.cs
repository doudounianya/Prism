using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.Services.Diagnostics;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.Prism;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Windows.Threading;

namespace Company.PlcHmi.Main.ViewModels;

/// <summary>
/// 操作日志页复用单例视图及上次结果；可见时才查询本地文件，不申请任何 PLC 采样。
/// 查询取消及身份版本共同阻止旧响应回填，注销后即清除日志内容。
/// </summary>
public sealed class OperationLogViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    public string LogNote => "通讯写入成功不代表设备动作完成。记录沿用软件日志保存期限。";
    private readonly OperationLogReader _reader;
    private readonly IUserAccessService _access;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _visible, _busy;
    private int _page = 1;
    private const int PageSize = 100;
    private IReadOnlyList<OperationLogEntry> _result = [];
    private DateTime? _from = DateTime.Today, _to = DateTime.Today;
    private string _user = "", _keyword = "", _status = "请选择日期并查询操作日志。";
    public bool KeepAlive => true;
    public DateTime? FromDate { get => _from; set => this.RaiseAndSetIfChanged(ref _from, value); }
    public DateTime? ToDate { get => _to; set => this.RaiseAndSetIfChanged(ref _to, value); }
    public string UserName { get => _user; set => this.RaiseAndSetIfChanged(ref _user, value); }
    public string Keyword { get => _keyword; set => this.RaiseAndSetIfChanged(ref _keyword, value); }
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public bool IsBusy { get => _busy; private set { this.RaiseAndSetIfChanged(ref _busy, value); Notify(); } }
    public bool CanQuery => _visible && !IsBusy && _access.CanOpenPage(ViewNames.OperationLogView);
    public bool CanCancel => _request is not null;
    public bool CanPrevious => CanQuery && _page > 1;
    public bool CanNext => CanQuery && _page * PageSize < _result.Count;
    public string Paging => _result.Count == 0 ? "0 条记录" : $"第 {_page} / {(_result.Count + PageSize - 1) / PageSize} 页 · 共 {_result.Count} 条";
    public ObservableCollection<OperationLogEntry> Items { get; } = [];
    public ICommand QueryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    public OperationLogViewModel(OperationLogReader reader, IUserAccessService access)
    {
        _reader = reader; _access = access; _dispatcher = System.Windows.Application.Current.Dispatcher;
        var scheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(_dispatcher));
        QueryCommand = ReactiveCommand.CreateFromTask(QueryAsync, outputScheduler: scheduler);
        CancelCommand = ReactiveCommand.Create(CancelQuery, outputScheduler: scheduler);
        PreviousCommand = ReactiveCommand.Create(() => ChangePage(-1), outputScheduler: scheduler);
        NextCommand = ReactiveCommand.Create(() => ChangePage(1), outputScheduler: scheduler);
        access.Changed += (_, _) =>
        {
            void ClearIdentity()
            {
                CancelQuery(); _result = []; Items.Clear(); _page = 1;
                Status = "登录身份已变化，请重新查询。"; Notify();
            }
            if (_dispatcher.CheckAccess()) ClearIdentity(); else _dispatcher.InvokeAsync(ClearIdentity);
        };
    }

    public async Task QueryAsync()
    {
        if (!CanQuery) return;
        if (FromDate is null || ToDate is null || ToDate.Value.Date < FromDate.Value.Date ||
            (ToDate.Value.Date - FromDate.Value.Date).TotalDays > 30 || ToDate.Value.Date == DateTime.MaxValue.Date)
        { Status = "请选择有效的开始、结束日期，最多查询 31 个自然日。"; return; }
        var query = new OperationLogQuery(new DateTimeOffset(FromDate.Value.Date).ToUniversalTime(),
            new DateTimeOffset(ToDate.Value.Date.AddDays(1)).ToUniversalTime(), UserName ?? "", Keyword ?? "");
        var request = new CancellationTokenSource(); _request = request;
        long generation = ++_generation, version = _access.Version;
        IsBusy = true; Status = "正在读取操作日志…";
        try
        {
            var result = await _reader.QueryAsync(query, request.Token);
            if (!_visible || generation != _generation || version != _access.Version || !_access.CanOpenPage(ViewNames.OperationLogView)) return;
            _result = result.Items; _page = 1; ShowPage();
            Status = result.Items.Count == 0 ? result.SkippedFiles > 0 || result.InvalidRecords > 0
                ? "没有取得完整操作记录，请检查日志文件。" : "所选条件没有操作记录。" : "查询完成。";
            if (result.Truncated) Status += "仅显示最新 10000 条，请缩小日期或筛选范围。";
            if (result.SkippedFiles > 0 || result.InvalidRecords > 0)
                Status += $"部分记录未读取：不可读文件 {result.SkippedFiles} 个，损坏或未写完记录 {result.InvalidRecords} 条。";
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "查询已取消。"; }
        catch (UnauthorizedAccessException) { if (generation == _generation) Status = "请登录后查看操作日志。"; }
        catch (Exception ex)
        {
            if (generation == _generation) Status = "日志读取失败，请检查日志目录访问权限；保留上次结果。";
            // 不将文件路径、普通日志内容或原始 JSON 放入界面/错误记录。
            Company.Logger.Logs.LogWarning("操作日志查询失败：" + ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            if (generation == _generation) IsBusy = false;
            request.Dispose(); Notify();
        }
    }

    public void CancelQuery()
    {
        var old = _request; _request = null; ++_generation;
        old?.Cancel(); IsBusy = false;
        if (old is not null) Status = "查询已取消；保留上次结果。";
        Notify();
    }
    private void ChangePage(int delta)
    {
        if (delta == -1 ? !CanPrevious : delta != 1 || !CanNext) return;
        _page += delta; ShowPage();
    }
    private void ShowPage()
    {
        // 操作记录不可变，同一 Id 不重建行；查询期间不清表，避免切页出现空白闪动。
        RetainedRows.Synchronize(Items, _result.Skip((_page - 1) * PageSize).Take(PageSize).ToArray(),
            row => row.Id, (_, _) => { });
        Notify();
    }
    private void Notify()
    {
        this.RaisePropertyChanged(nameof(CanQuery)); this.RaisePropertyChanged(nameof(CanCancel));
        this.RaisePropertyChanged(nameof(CanPrevious)); this.RaisePropertyChanged(nameof(CanNext)); this.RaisePropertyChanged(nameof(Paging));
    }
    public void SetVisible(bool visible)
    {
        visible &= _access.CanOpenPage(ViewNames.OperationLogView);
        if (_visible == visible) return;
        _visible = visible;
        if (visible) _ = QueryAsync(); else CancelQuery();
        Notify();
    }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedTo(NavigationContext context) => SetVisible(true);
    public void OnNavigatedFrom(NavigationContext context) => SetVisible(false);
}

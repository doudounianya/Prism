/*
 * 文件职责与维护说明：
 * 主界面状态栏及账号动作协调器：显示运行、通讯和历史保存状态，处理原窗口内登录与退出。
 * 退出登录只撤销身份和受限页面访问，不关闭窗口、不停止通讯或历史服务。
 */
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.Prism;
using Company.Logger;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Windows.Threading;
using Company.PlcHmi.Shared.Login;
using Prism.Ioc;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Cache;
using Company.Core.Ioc;
using Company.Platform.Shared.Model;
using System.Reactive.Concurrency;
using System.Windows;
using Company.PlcHmi.Main.Presentation;

namespace Company.PlcHmi.Main.ViewModels;

public sealed class MainViewModel : ReactiveObject
{
    private readonly IPlcRuntime _runtime;
    private readonly IPlcBindingConfigProvider _configuration;
    private readonly IRegionManager _regions;
    private readonly DispatcherTimer _timer;
    private readonly ISession _session;
    private readonly IUserAccessService _access;
    private readonly ICacheManager _cache;
    private readonly IPlcDataProvider _data;
    private string? _modeReadoutStamp;
    private PlcVariableKey[] _modeReadoutKeys = [];
    private string _equipmentModeText = "未知", _equipmentStateText = "未知", _modeReadoutHint = "尚无有效模式反馈";
    public string EquipmentModeText { get => _equipmentModeText; private set => this.RaiseAndSetIfChanged(ref _equipmentModeText, value); }
    public string EquipmentStateText { get => _equipmentStateText; private set => this.RaiseAndSetIfChanged(ref _equipmentStateText, value); }
    public string ModeReadoutHint { get => _modeReadoutHint; private set => this.RaiseAndSetIfChanged(ref _modeReadoutHint, value); }
    private bool _signingOut;
    private AccountSnapshot _accountSnapshot;
    private bool _accountRefreshed;
    public bool IsLoggedIn => _access.IsLoggedIn;
    public string AccountToolTip => IsLoggedIn ? "修改本人密码" : "登录账号";
    public string PermissionText => MaintenanceInterlock.Blocked ? "维护锁定 · 可查看及验证解锁" :
        UserAccessRules.CanEditParameters(_session.CurrentUser) && !_access.CanEditParameters ? "需进入调试 · 参数只读" : PermissionTextFor(_access.CanEditParameters, _access.CanControlModes);
    public System.Windows.Input.ICommand AccountCommand { get; }
    public System.Windows.Input.ICommand SignOutCommand { get; }
    private readonly Company.PlcHmi.Shared.PLC.History.IPlcHistoryLifecycle _history;
    private readonly Company.PlcHmi.Main.Services.History.PlcHistoryOptions _historyOptions;
    private string _historyText = "";
    public string HistoryText { get => _historyText; private set => this.RaiseAndSetIfChanged(ref _historyText, value); }
    private string _statusText = "", _configurationText = "", _diagnostics = "";
    private string _accountText = "本机终端 · 未登录", _plcFeedbackText = "PLC反馈：未连接";
    public string AccountText { get => _accountText; private set => this.RaiseAndSetIfChanged(ref _accountText, value); }
    public string PlcFeedbackText { get => _plcFeedbackText; private set => this.RaiseAndSetIfChanged(ref _plcFeedbackText, value); }
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public string ConfigurationText { get => _configurationText; private set => this.RaiseAndSetIfChanged(ref _configurationText, value); }
    public string Diagnostics { get => _diagnostics; private set => this.RaiseAndSetIfChanged(ref _diagnostics, value); }
    public System.Windows.Input.ICommand ChangePasswordCommand { get; } = ReactiveCommand.Create(() =>
        Company.Core.Ioc.PrismProvider.DialogService.ShowDialog(ViewNames.ChangePasswordView), outputScheduler: System.Reactive.Concurrency.Scheduler.Immediate);
    public MainViewModel(IPlcRuntime runtime, IPlcBindingConfigProvider configuration, IRegionManager regions,
        Company.PlcHmi.Shared.PLC.History.IPlcHistoryLifecycle history,
        Company.PlcHmi.Main.Services.History.PlcHistoryOptions historyOptions,
        ISession session, IUserAccessService access, ICacheManager cache, IPlcDataProvider data)
    {
        _runtime = runtime; _configuration = configuration; _regions = regions;
        _history = history; _historyOptions = historyOptions;
        _session = session; _access = access; _cache = cache; _data = data;
        // 保留首次激活前的默认账号文字；先记录初始权限，避免首轮轮询重复通知未变化的属性。
        _accountSnapshot = ReadAccountSnapshot();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        AccountCommand = ReactiveCommand.Create(() =>
        {
            if (_signingOut) return;
            PrismProvider.DialogService.ShowDialog(IsLoggedIn ? ViewNames.ChangePasswordView : ViewNames.SignInView);
        }, outputScheduler: Scheduler.Immediate);
        SignOutCommand = ReactiveCommand.CreateFromTask(SignOutAsync,
            outputScheduler: new SynchronizationContextScheduler(new DispatcherSynchronizationContext(_timer.Dispatcher)));
        _access.Changed += (_, _) =>
        {
            // 事件立即复核访问权限；即使服务仅改变页面规则、未改变显示字段，也不能延迟撤权。
            if (_timer.Dispatcher.CheckAccess()) RefreshAccount(forceNavigationCheck: true);
            else _timer.Dispatcher.InvokeAsync(() => RefreshAccount(forceNavigationCheck: true));
        };
    }
    // 先替换为空身份，使各权限订阅者立即停止新操作，再在后台保存关闭自动登录的偏好。
    // 保留窗口、运行服务和历史消费者；只有当前页面不属于公开页面时才切回设备监控。
    private async Task SignOutAsync()
    {
        if (_signingOut || !IsLoggedIn) return;
        _signingOut = true;
        // 先撤销当前身份和操作权限，后台通讯、历史保存及主窗口保持运行。
        _session.CurrentUser = new CurrentUser();
        RefreshAccount();
        try { await Task.Run(() => _cache.Set(CacheKey.IsAutoLogin, false)); }
        catch { Logs.LogWarning("已退出登录，但自动登录偏好未能保存。"); }
        finally { _signingOut = false; }
    }
    // 账号显示与菜单/操作权限使用同一个会话来源。
    // 身份变化后复核当前工作区目标，防止原先打开的工程页面在退出后继续可见。
    private void RefreshAccount(bool forceNavigationCheck = false)
    {
        // 每秒仍核对实际身份和权限值，兼容未发布 Changed 的会话字段修改；不缓存密码等敏感内容。
        var previous = _accountSnapshot;
        var current = ReadAccountSnapshot();
        var accountChanged = current != previous;
        _accountSnapshot = current;
        if (!_accountRefreshed || current.IsLoggedIn != previous.IsLoggedIn ||
            current.UserName != previous.UserName || current.Role != previous.Role)
            AccountText = current.IsLoggedIn ? $"{current.UserName} · {UserRoles.Name(current.Role)}" : "未登录 · 点击登录";
        if (current.IsLoggedIn != previous.IsLoggedIn)
        {
            this.RaisePropertyChanged(nameof(IsLoggedIn));
            this.RaisePropertyChanged(nameof(AccountToolTip));
        }
        if (current.AccessVersion != previous.AccessVersion || PermissionTextFor(current.CanEditParameters, current.CanControlModes) !=
            PermissionTextFor(previous.CanEditParameters, previous.CanControlModes))
            this.RaisePropertyChanged(nameof(PermissionText));
        // 无身份或权限变化的轮询无需反复扫描区域；首次刷新、真实变化和访问事件仍保留导航保护。
        var checkNavigation = !_accountRefreshed || accountChanged || forceNavigationCheck;
        _accountRefreshed = true;
        if (!checkNavigation) return;
        if (_regions.Regions.ContainsRegionWithName(RegionNames.WorkspaceRegion) &&
            _regions.Regions[RegionNames.WorkspaceRegion].ActiveViews.OfType<FrameworkElement>().Any(v => !_access.CanOpenPage(v.GetType().Name)))
            _regions.RequestNavigate(RegionNames.WorkspaceRegion, ViewNames.DeviceMonitorView);
    }
    private AccountSnapshot ReadAccountSnapshot()
    {
        var user = _session.CurrentUser;
        return new AccountSnapshot(user?.Id ?? 0, user?.UserName ?? "", user?.Role ?? 0, _access.IsLoggedIn,
            _access.CanEditParameters, _access.CanControlModes, _access.IsAdministrator, _access.Version);
    }
    private static string PermissionTextFor(bool canEditParameters, bool canControlModes) =>
        canEditParameters ? "参数与操作" : canControlModes ? "操作员 · 参数只读" : "未登录 · 只读查看";
    private readonly record struct AccountSnapshot(int UserId, string UserName, int Role, bool IsLoggedIn,
        bool CanEditParameters, bool CanControlModes, bool IsAdministrator, long AccessVersion);
    public void Activate()
    {
        RefreshStatus(); _timer.Start();
        if (_regions.Regions.ContainsRegionWithName(RegionNames.WorkspaceRegion) &&
            !_regions.Regions[RegionNames.WorkspaceRegion].ActiveViews.Any())
            _regions.RequestNavigate(RegionNames.WorkspaceRegion, ViewNames.DeviceMonitorView,
                result => { if (result.Exception is not null) Logs.LogError(result.Exception); });
    }
    public void Deactivate() => _timer.Stop();
    // 状态栏读取既有运行与历史快照，不额外建立连接，也不借“通讯成功”推断设备模式。
    // 最近通信时间、重连尝试与保存错误保持可追踪，便于现场诊断。
    public void RefreshStatus()
    {
        var status = _runtime.Status;
        var connected = status.Devices.Count(d => d.State == PlcDeviceState.Connected);
        var connectionHint = status.State != PlcRuntimeState.Running ? " · 通讯服务未运行" :
            status.Devices.Any(d => d.State == PlcDeviceState.Faulted) ? " · 断线，自动重连中" :
            status.Devices.Any(d => d.State == PlcDeviceState.Connecting) ? " · 连接检测中" : "";
        PlcFeedbackText = status.Devices.Count == 0 ? "PLC通讯：无启用设备" :
            $"PLC通讯：{connected} / {status.Devices.Count} 台正常{connectionHint}";
        RefreshAccount();
        StatusText = $"PLC：{status.State}   " + (status.Devices.Count == 0 ? "无启用设备，未建立连接" :
            string.Join(" | ", status.Devices.Select(d => $"{d.DeviceId}：{ConnectionStateText(d.State)}（会话 {d.ConnectionEpoch}，连接尝试 {d.ConnectionAttempts} 次）" +
                $" 最近通讯成功：{(d.LastCommunicationAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未成功")} {d.Error}")));
        var config = _configuration.Current;
        RefreshModeReadout(config);
        ConfigurationText = config is null ? "配置尚未加载成功" :
            $"配置：{config.FilePath}  |  {config.Version}  |  SHA256 {config.FileSha256}  |  启用变量 {config.ActiveVariables.Count()}";
        Diagnostics = string.Join(Environment.NewLine, status.Diagnostics.Select(d => $"{d.Code}：{d.Message}"));
        var history = _history.Status;
        var applied = history.Production?.Applied;
        // 字节占用与待提交数量分开显示，避免把“文件大”直接解释为“所有记录都没入库”。
        // 所有诊断值均来自现有服务快照，不在 UI 刷新中访问文件系统或数据库。
        var journalUsage = history.JournalCapacityBytes > 0 ?
            $" | 本地日志 {history.JournalBytes / 1048576d:F1}/{history.JournalCapacityBytes / 1048576d:F1} MiB" : "";
        var pendingLimit = history.MaximumPending > 0 ? $"/{history.MaximumPending}" : "";
        var lastCommit = history.LastCommittedUtc is { } savedAt ? savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未成功提交";
        HistoryText = $"报警保存：{(_historyOptions.AlarmCaptureEnabled ? "已启用" : "关闭")} | 持久化：{history.State} | 本地待提交 {history.Pending}{pendingLimit} | 小时同步：{history.Production?.State}" +
            (applied is null ? "" : $"（新增 {applied.Added} / 更新 {applied.Updated} / 未变 {applied.Unchanged} / 拒绝 {applied.Rejected}）") +
            journalUsage + (history.NearCapacity ? " · 保存容量预警" : "") + $" | 最近入库成功：{lastCommit} {history.Error}" +
            (history.JournalError is { Length: > 0 } localError ? $" | 本地追加失败：{localError}" : "");
    }
    /// <summary>状态栏仅消费现有页面采集产生的缓存，不新建范围、计时器或后台通讯。
    /// 连接交接/离页保留最近有效显示，缓存来源放在提示中；这些文字不用于任何写入许可。</summary>
    private void RefreshModeReadout(PlcBindingConfiguration? config)
    {
        var stamp = config is null ? "" : config.Version + ":" + config.FileSha256;
        if (_modeReadoutStamp != stamp)
        {
            _modeReadoutStamp = stamp;
            var bindings = config?.ActiveVariables.Where(b => b.ComponentId == "UnitMode[0]" && b.Access.HasFlag(PlcAccess.Read) &&
                b.LogicalId is "UnitMode[0].Mode" or "UnitMode[0].Status").OrderBy(b => b.DeviceId).ToArray() ?? [];
            var device = bindings.FirstOrDefault()?.DeviceId;
            _modeReadoutKeys = bindings.Where(b => b.DeviceId == device).Select(b => new PlcVariableKey(b.Protocol, b.DeviceId, b.LogicalId)).ToArray();
            EquipmentModeText = EquipmentStateText = "未知";
        }
        var snapshots = _data.GetSnapshots(_modeReadoutKeys);
        int? Number(string logical)
        {
            var sample = snapshots.FirstOrDefault(s => s.Key.LogicalId == logical && s.ConfigurationVersion == stamp);
            var value = sample?.IsValid == true && sample.Quality == PlcObservationQuality.Good ? sample.EngineeringValue : sample?.LastValidValue;
            return int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var number) ? number : null;
        }
        EquipmentModeText = ModeControlDisplay.ModeName(Number("UnitMode[0].Mode"));
        EquipmentStateText = ModeControlDisplay.StatusName(Number("UnitMode[0].Status"));
        bool fresh = snapshots.Count == 2 && snapshots.All(s => s.IsValid && s.Quality == PlcObservationQuality.Good && s.ConfigurationVersion == stamp &&
            _runtime.Status.State == PlcRuntimeState.Running && _runtime.Status.Devices.Any(d => d.DeviceId == s.Key.DeviceId && d.State == PlcDeviceState.Connected && d.ConnectionEpoch == s.ConnectionEpoch));
        ModeReadoutHint = fresh ? "当前页面采集的有效模式／状态反馈" : "上次有效模式／状态；当前页面未刷新或连接尚未确认。设备操作仍需当前有效反馈。";
    }
    private static string ConnectionStateText(PlcDeviceState state) => state switch
    {
        PlcDeviceState.Connected => "通讯正常",
        PlcDeviceState.Connecting => "正在连接并检测",
        PlcDeviceState.Faulted => "通讯异常，等待自动重连",
        _ => "未连接"
    };
}

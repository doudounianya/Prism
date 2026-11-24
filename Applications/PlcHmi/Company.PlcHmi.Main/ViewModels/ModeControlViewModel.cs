using Company.Hmi.Contracts;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.Services.Diagnostics;
using Company.PlcHmi.Main.Services.PLC;
using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Logger;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Globalization;
using System.Windows.Threading;
using System.Collections.ObjectModel;

namespace Company.PlcHmi.Main.ViewModels;

/// <summary>
/// 本机 st_UnitMode[0] 的页面会话。沿用已有配置、按页采集、写入、登录权限和操作记录服务，
/// 不建立独立连接、不注册新的后台采样、不改变现有采集周期。
/// 状态图不模拟 PLC 状态转移：按钮成功后只临时更新命令位颜色，i_Mode/i_Status 始终以读取为准。
/// </summary>
public sealed class ModeControlViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime, IModeControlPage
{
    IModeDiagramFeedback IModeControlPage.Feedback => Display;
    public string FeedbackHeading => "PLC 反馈信号";
    public string RunExplanation => "运行继续校验与初始化完成均来自PLC反馈。单次结束 b_Signal[3]、停止按钮或旋钮手动任一成立，运行返回停止；PLC 在停止状态复位单次结束信号。";
    public string MaintenanceExplanation => "维护由每人分别验证上锁、解锁；退出主账户不会解除人员锁。最后一人解锁后先回停止，再按当前故障、暂停等条件重判。";
    public string RecoveryExplanation => "暂停解除先回停止；运行、调试、初始化请求保持有效时，会再次参与判断。急停状态与停止状态本身都不代表机构已经停稳，实际停稳及安全复位需另行确认。";
    private const string Component = "UnitMode[0]";
    private readonly ComponentPageSession _page;
    private readonly IPlcBindingConfigProvider _configuration;
    private readonly IPlcRuntime _runtime;
    private readonly IPlcWriteService _writer;
    private readonly IPlcDataProvider _data;
    private readonly IUserAccessService _access;
    private readonly ISession _session;
    private readonly IUserAccountService _accounts;
    public ObservableCollection<MaintenanceInterlock.Person> MaintenancePeople { get; } = [];
    public bool CanAdminUnlock => _access.IsAdministrator;
    public string MaintenanceSummary => MaintenanceInterlock.Blocked
        ? $"维护锁定 · {MaintenancePeople.Count(p => !p.Releasing)} 人持锁，设备操作已禁用。最后一人解锁后仍需 PLC 确认退出。"
        : "当前无人员锁。上锁需本人验证；首人上锁后请求进入维护。";
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _stopPress;
    private (PlcWriteContext Context, PlcVariableKey Key, OperationAudit.Actor Actor)? _stopTarget;
    public bool IsStopHeld { get; private set; }
    private CancellationTokenSource? _initializationPress;
    private (PlcWriteContext Context, PlcVariableKey Key, OperationAudit.Actor Actor)? _initializationTarget;
    private (PlcWriteContext Context, PlcVariableKey Key, OperationAudit.Actor Actor)? _debugOwner;
    private OperationAudit.Actor _debugIdentity;
    // 身份变更或成功退出调试之前的 True 缓存不能重新开放参数入口。
    // 使用原子时间戳，兼容登录权限通知与页面刷新来自不同线程的情况。
    private long _debugFeedbackAfterTicks = DateTimeOffset.UtcNow.UtcTicks;
    private bool _initializationHeld;
    public bool IsInitializationHeld => _initializationHeld;
    private void SetInitializationHeld(bool value)
    {
        if (_initializationHeld == value) return;
        _initializationHeld = value;
        this.RaisePropertyChanged(nameof(IsInitializationHeld)); this.RaisePropertyChanged(nameof(CanRequestInitialization));
    }
    private CancellationTokenSource _target = new();
    private readonly Dictionary<string, PlcDisplayField> _fields = new(StringComparer.Ordinal);
    private PlcDisplayField[] _readFields = [];
    private string? _stamp, _device;
    private long _epoch, _revision;
    private bool _active, _visible, _observing, _ready, _writing;
    private int _refreshQueued;
    private PlcRuntimeState? _runtimeState;
    private int? _mode, _status;
    private ModeControlDisplay _display = ModeControlDisplay.Empty;
    private string? _confirmedDiagram;
    private (int Mode, int Status)? _confirmedState;
    private string _message = "模式控制绑定尚未加载。", _operationMessage = "";

    public bool KeepAlive => true;
    // 只读行一次性创建，刷新仅更新属性。Action[5] 不再作为急停显示或许可依据。
    public IReadOnlyList<ModeSignalItem> PhysicalInputs { get; } =
    [
        new("手自动旋钮", "Action[0]", "b_Action[0]", "自动", "手动"),
        new("运行按钮", "Action[1]", "b_Action[1]", "已按下", "未按下"),
        new("暂停按钮", "Action[2]", "b_Action[2]", "已按下", "未按下"),
        new("停止按钮", "Action[3]", "b_Action[3]", "已按下", "未按下"),
        new("复位按钮", "Action[4]", "b_Action[4]", "已按下", "未按下")
    ];
    public IReadOnlyList<ModeSignalItem> InterfaceRequests { get; } =
    [
        new("调试请求", "Action[6]", "b_Action[6]", "已置位", "已清除"),
        new("初始化请求", "Action[7]", "b_Action[7]", "请求中", "未请求"),
        new("维护请求", "Action[8]", "b_Action[8]", "已置位", "已清除")
    ];
    public IReadOnlyList<ModeSignalItem> PlcSignals { get; } =
    [
        new("初始化完成", "Signal[0]", "b_Signal[0]", "已完成", "未完成"),
        new("暂停信号", "Signal[1]", "b_Signal[1]", "已触发", "未触发"),
        new("故障信号", "Signal[2]", "b_Signal[2]", "已触发", "未触发"),
        new("单次结束", "Signal[3]", "b_Signal[3]", "已结束", "未结束"),
        new("运行继续校验", "Signal[4]", "b_Signal[4]", "已完成", "未完成"),
        new("急停信号", "Signal[5]", "b_Signal[5]", "急停", "未触发")
    ];
    public ModeControlDisplay Display { get => _display; private set => this.RaiseAndSetIfChanged(ref _display, value); }
    /// <summary>只有当前页面取得同一连接/配置的有效模式、状态时，才提供自动跟随目标。
    /// 与写入许可独立；只读账户或未开启写入时同样可以跟随。缓存和断线不触发自动切页。</summary>
    public string? ConfirmedDiagram { get => _confirmedDiagram; private set => this.RaiseAndSetIfChanged(ref _confirmedDiagram, value); }
    /// <summary>模式与状态作为同一份有效反馈发布。即使目标图相同，状态变化也可触发跟随；
    /// 相同值不会重复通知，避免稳定刷新反复打断人工查看。</summary>
    public (int Mode, int Status)? ConfirmedState { get => _confirmedState; private set => this.RaiseAndSetIfChanged(ref _confirmedState, value); }
    /// <summary>触摸操作入口与图中按钮复用同一许可规则；可查看手动图不代表 PLC 已切换到手动。</summary>
    public bool CanToggleDebug => UserAccessRules.CanEditParameters(_session.CurrentUser) && Display.TryRequest(Display.Action(6) == true ? "DebugExit" : "DebugEnter", out _, out _);
    // 按住后 PLC 可能进入初始化状态或请求位变 True；仍需保留控件捕获，以接收松开事件。
    public bool CanRequestInitialization => IsInitializationHeld || Display.TryRequest("Initialize", out _, out _);
    private string _modeOperationHint = "等待当前连接的有效模式反馈。";
    public string ModeOperationHint { get => _modeOperationHint; private set => this.RaiseAndSetIfChanged(ref _modeOperationHint, value); }
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string OperationMessage { get => _operationMessage; private set => this.RaiseAndSetIfChanged(ref _operationMessage, value); }
    public string ModeText => ModeName(Display.Mode);
    public string StatusText => StatusName(Display.Status);
    private static string ModeName(int? mode) => ModeControlDisplay.ModeName(mode);
    private static string StatusName(int? status) => ModeControlDisplay.StatusName(status);
    public string KnobText => Physical(0, "自动", "手动");
    public string RunText => Physical(1);
    public string PauseText => Physical(2);
    public string StopText => Physical(3);
    public string ResetText => Physical(4);
    public string EmergencyText => SignalText(5, "急停", "未触发");
    public string InitializationText => SignalText(0, "已完成", "未完成");
    public string PauseSignalText => SignalText(1, "已触发", "未触发");
    public string FaultSignalText => SignalText(2, "已触发", "未触发");
    private string Physical(int index, string on = "已按下", string off = "未按下") => Display.Action(index) switch { true => on, false => off, _ => "—" };
    private string SignalText(int index, string on, string off) => Display.Signal(index) switch { true => on, false => off, _ => "—" };

    public ModeControlViewModel(ComponentPageSession page, IPlcBindingConfigProvider configuration,
        IPlcRuntime runtime, IPlcWriteService writer, IPlcDataProvider data, IUserAccessService access, ISession session, IUserAccountService accounts)
    {
        _page = page; _configuration = configuration; _runtime = runtime; _writer = writer; _data = data; _access = access; _session = session;
        _accounts = accounts;
        _debugIdentity = OperationAudit.CaptureActor(session);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _access.Changed += AccessChanged;
        MaintenanceInterlock.Changed += (_, _) => _timer.Dispatcher.InvokeAsync(UpdateMaintenancePeople);
        UpdateMaintenancePeople();
    }

    private void UpdateMaintenancePeople()
    {
        var people = MaintenanceInterlock.People;
        // 只在人员锁变化时更新列表，不随 UI 计时器重建内容。
        MaintenancePeople.Clear(); foreach (var person in people) MaintenancePeople.Add(person);
        this.RaisePropertyChanged(nameof(MaintenanceSummary));
    }

    private void AccessChanged(object? sender, EventArgs e)
    {
        var identity = OperationAudit.CaptureActor(_session);
        if (Interlocked.Exchange(ref _debugIdentity, identity) != identity)
            Interlocked.Exchange(ref _debugFeedbackAfterTicks, DateTimeOffset.UtcNow.UtcTicks);
        // 退出登录/权限变化立即取消仍在排队的写入；后续发送还必须重新校验 Version。
        _target.Cancel();
        _ = _timer.Dispatcher.InvokeAsync(async () =>
        {
            RenewTarget(); await ReleaseMomentaryRequestsAsync();
            if (_debugOwner is { } owner && (!_access.IsLoggedIn || !UserAccessRules.CanEditParameters(_session.CurrentUser) ||
                _session.CurrentUser.Id != owner.Actor.Id || _session.CurrentUser.UserName != owner.Actor.Name))
                await ReleaseDebugOnIdentityChangeAsync();
            Publish(); this.RaisePropertyChanged(nameof(CanAdminUnlock)); this.RaisePropertyChanged(nameof(CanToggleDebug));
        }, DispatcherPriority.Background);
    }
    private void RenewTarget()
    {
        var previous = _target; _target = new CancellationTokenSource(); previous.Cancel(); previous.Dispose();
    }

    public async Task SetVisibleAsync(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        await ObserveAsync();
    }
    private async Task ObserveAsync()
    {
        var observing = _active && _visible;
        if (!observing) await ReleaseMomentaryRequestsAsync();
        if (_observing != observing)
        {
            _observing = observing;
            if (observing) { _page.ValuesChanged += ValuesChanged; _timer.Start(); }
            else { _page.ValuesChanged -= ValuesChanged; _timer.Stop(); }
        }
        LoadCatalog();
        await ApplySelectionAsync();
    }
    private void ValuesChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0 || _timer.Dispatcher.HasShutdownStarted) return;
        _timer.Dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (_observing) Refresh(); }, DispatcherPriority.Background);
    }

    /// <summary>只在配置更换时重建字段；页签、导航和普通采样更新均复用原字段实例。</summary>
    private bool LoadCatalog()
    {
        var configuration = _configuration.Current;
        var stamp = configuration is null ? null : configuration.Version + ":" + configuration.FileSha256;
        if (_stamp == stamp) return false;
        _stamp = stamp; _fields.Clear(); _mode = null; _status = null;
        var candidates = configuration?.ActiveVariables.Where(v => v.Category == "设备状态" && v.ComponentId == Component).ToArray() ?? [];
        _device = candidates.Select(v => v.DeviceId).Distinct().Order().FirstOrDefault();
        foreach (var binding in candidates.Where(v => v.DeviceId == _device))
            _fields.Add(binding.LogicalId, new PlcDisplayField(binding, binding.Access.HasFlag(PlcAccess.Read) ? "" : "仅写命令"));
        _readFields = _fields.Values.Where(f => f.CanRead && f.LogicalId != Component + ".Action[5]").ToArray();
        return true;
    }

    /// <summary>连接代次或显示范围变化后，取消旧操作并只申请本页反馈字段；不增加后台采样。</summary>
    private async Task ApplySelectionAsync()
    {
        await ReleaseMomentaryRequestsAsync();
        var revision = ++_revision; _ready = false; RenewTarget();
        bool active = _active && _visible && _runtime.Status.State == PlcRuntimeState.Running && _readFields.Length > 0;
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == _device);
        _epoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
        _runtimeState = _runtime.Status.State;
        var requestedAt = DateTimeOffset.UtcNow;
        foreach (var field in _readFields)
            if (active) field.BeginRefresh(_epoch, _stamp ?? "", requestedAt); else field.MarkCached(_observing ? "采集未启动" : "页面未显示");
        Publish();
        try
        {
            await _page.SetAsync(_readFields, active);
            if (revision != _revision) return;
            _ready = active; RefreshSnapshots();
            // 首次进入立即请求一次真实读取，不等待页面 UI 计时器或下一轮采样。
            if (active) { await _page.RefreshAsync(); if (revision == _revision) RefreshSnapshots(); }
        }
        catch (Exception ex) { if (revision == _revision) { _ready = false; Message = "模式控制采集申请失败：" + ex.Message; Publish(); } Logs.LogError(ex); }
    }
    private async void Refresh()
    {
        if (!_observing) return;
        try
        {
            var changed = LoadCatalog();
            var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == _device);
            var epoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
            if (changed || _runtimeState != _runtime.Status.State || epoch != _epoch) await ApplySelectionAsync();
            else RefreshSnapshots();
        }
        catch (Exception ex) { Message = "模式控制刷新失败：" + ex.Message; Logs.LogError(ex); }
    }
    private void RefreshSnapshots()
    {
        if (_active && _visible && _ready)
        {
            var lookup = _page.Snapshots().ToDictionary(v => v.Key);
            foreach (var field in _readFields)
            {
                var snapshot = lookup.GetValueOrDefault(field.Key); field.Apply(snapshot);
                if (field.LogicalId == Component + ".Mode") UpdateEnum(field, snapshot, ref _mode);
                if (field.LogicalId == Component + ".Status") UpdateEnum(field, snapshot, ref _status);
            }
            // 维护退出确认完全使用真实反馈，不能采用按钮写入成功后的临时显示值。
            var feedback = new[] { "Mode", "Status", "Action[8]" }.Select(s => Field(s)).ToArray();
            if (feedback.All(f => f?.HasCurrentValue == true) && _mode is { } mode && _status is { } status &&
                Field("Action[8]")?.BooleanValue is { } request)
            {
                var samples = feedback.Select(f => lookup.GetValueOrDefault(f!.Key)).ToArray();
                if (samples.All(s => s?.IsValid == true && s.ConnectionEpoch == _epoch && s.ConfigurationVersion == _stamp && s.LastValidAt.HasValue))
                    MaintenanceInterlock.Observe(request, mode, status, samples.Min(s => s!.LastValidAt!.Value));
            }
            ObserveExistingDebugSession(lookup);
        }
        if (IsStopHeld && (!_ready || new[] { "Mode", "Status", "Action[3]" }.Any(s => Field(s)?.HasCurrentValue != true))) _ = ReleaseStopAsync();
        // 按住期间急停/读取有效性改变也撤销请求；清零仍固定原连接，不替代 PLC 自身联锁。
        if (IsInitializationHeld && (!Fresh() || Field("Signal[5]")?.BooleanValue != false)) _ = ReleaseInitializationAsync();
        Message = _readFields.Length == 0 ? "当前配置中没有启用的 st_UnitMode[0] 绑定。" :
            MaintenanceInterlock.Blocked ? "维护锁定：可查看监控、报警、生产数据和日志；设备操作和参数下发已禁用。" :
            !_observing ? "页面未显示，模式控制采集已释放。" :
            _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == _device)?.State != PlcDeviceState.Connected ? "PLC 未连接；保留上次显示，操作不可用。" :
            !Fresh() ? "等待模式控制当前连接的有效读取，操作暂不可用。" :
            !_writer.Enabled ? "当前为只读，PLC 写入开关未启用。" : !_access.CanControlModes ? "未登录或没有模式控制权限。" : "模式控制反馈已更新。";
        Publish();
    }
    /// <summary>
    /// 软件启动时或 PLC 侧已经进入调试，也应允许当前工程师/管理员操作参数。
    /// 此处只建立本机授权观察会话，不向 PLC 写 True，不增加采样；必须先取得本页当前范围、
    /// 当前身份及同一连接/配置的新有效反馈，再交给原 DebugParameterAccess 复核后续样本。
    /// </summary>
    private void ObserveExistingDebugSession(IReadOnlyDictionary<PlcVariableKey, PlcVariableSnapshot> lookup)
    {
        if (_device is null || new[] { "Mode", "Status", "Action[6]", "Signal[5]" }.Any(s => Field(s)?.HasCurrentValue != true)) return;
        ObserveParameterDebugFeedback(lookup.Values.ToArray(), _device);
    }
    /// <summary>
    /// 参数页可直接查看，因此也允许用该页已有的四项模式反馈识别 PLC 侧调试。
    /// 不打开模式页面、不启动其计时器或采集，不向 PLC 请求进入调试；注销清零仍复用本单例原有所有权逻辑。
    /// </summary>
    public void ObserveParameterDebugFeedback(IReadOnlyList<PlcVariableSnapshot> snapshots, string deviceId)
    {
        if (_writing || !_writer.Enabled || !_access.IsLoggedIn || MaintenanceInterlock.Blocked ||
            !UserAccessRules.CanEditParameters(_session.CurrentUser)) return;
        var config = _configuration.Current;
        if (config is null) return;
        var stamp = config.Version + ":" + config.FileSha256;
        var runtime = _runtime.Status;
        var device = runtime.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (runtime.State != PlcRuntimeState.Running || runtime.ConfigurationVersion != stamp || device?.State != PlcDeviceState.Connected) return;
        var actor = OperationAudit.CaptureActor(_session);
        // 原操作者的注销清零尚未结束时，不把所有权转给新登录人员。
        if (_debugOwner is { } previous && (previous.Actor.Id != actor.Id || previous.Actor.Name != actor.Name)) return;
        if (DebugParameterAccess.IsBoundTo(actor.Id, actor.Name, deviceId, device.ConnectionEpoch, stamp)) return;
        var after = new DateTimeOffset(Interlocked.Read(ref _debugFeedbackAfterTicks), TimeSpan.Zero);
        var samples = new[] { "Mode", "Status", "Action[6]", "Signal[5]" }
            .Select(s => snapshots.FirstOrDefault(v => v.Key.DeviceId == deviceId && v.Key.LogicalId == Component + "." + s)).ToArray();
        if (samples.Any(s => s?.IsValid != true || s.Quality != PlcObservationQuality.Good ||
            s.ConnectionEpoch != device.ConnectionEpoch || s.ConfigurationVersion != stamp || s.LastValidAt < after || !s.LastValidAt.HasValue)) return;
        if (!int.TryParse(Convert.ToString(samples[0]!.EngineeringValue, CultureInfo.InvariantCulture), out var mode) || mode != 0 ||
            !int.TryParse(Convert.ToString(samples[1]!.EngineeringValue, CultureInfo.InvariantCulture), out var status) || status != 30 ||
            samples[2]!.EngineeringValue is not true || samples[3]!.EngineeringValue is not false) return;
        var command = config.ActiveVariables.FirstOrDefault(b => b.DeviceId == deviceId && b.LogicalId == Component + ".Request.Debug" &&
            b.Access.HasFlag(PlcAccess.Write) && b.DataType == PlcDataType.Bool);
        if (command is null) return;
        PlcWriteContext context;
        // Capture 只校验并固定目标，不发起通讯；连接恰在检查期间变化时，等待下一轮有效反馈即可。
        try { context = _writer.Capture(deviceId); }
        catch (InvalidOperationException) { return; }
        if (context.ConnectionEpoch != device.ConnectionEpoch || context.ConfigurationStamp != stamp) return;
        BeginDebugSession(context, new(command.Protocol, deviceId, command.LogicalId), actor);
    }
    /// <summary>本机请求调试和识别既有 PLC 调试共用同一授权、原目标和注销清零逻辑。
    /// Begin 不直接授予权限；后续新读取确认手动/调试/请求 True/无急停后，参数操作才会开放。</summary>
    private void BeginDebugSession(PlcWriteContext context, PlcVariableKey key, OperationAudit.Actor actor)
    {
        _debugOwner = (context, key, actor);
        DebugParameterAccess.Begin(actor.Id, actor.Name, context.DeviceId, context.ConnectionEpoch, context.ConfigurationStamp, () =>
            _access.IsLoggedIn && UserAccessRules.CanEditParameters(_session.CurrentUser) &&
            _session.CurrentUser.Id == actor.Id && _session.CurrentUser.UserName == actor.Name &&
            _runtime.Status.State == PlcRuntimeState.Running && _runtime.Status.ConfigurationVersion == context.ConfigurationStamp &&
            _runtime.Status.Devices.Any(d => d.DeviceId == context.DeviceId && d.State == PlcDeviceState.Connected && d.ConnectionEpoch == context.ConnectionEpoch));
    }
    private static void UpdateEnum(PlcDisplayField field, PlcVariableSnapshot? snapshot, ref int? value)
    {
        if (field.HasCurrentValue && int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) value = code;
        else if (snapshot?.State is PlcValueState.Invalid or PlcValueState.Uncertain or PlcValueState.Stale) value = null;
        // Pending/Inactive 和正常离页不清除旧显示；不能因此恢复写入资格。
    }
    private PlcDisplayField? Field(string suffix) => _fields.GetValueOrDefault(Component + "." + suffix);
    private bool Fresh() => _ready && new[] { "Mode", "Status", "Signal[5]", "Action[6]", "Action[7]", "Action[8]" }.All(s => Field(s)?.HasCurrentValue == true);
    private void Publish()
    {
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == _device);
        string contextReason = !_observing ? "页面未显示" : _runtime.Status.State != PlcRuntimeState.Running ? "采集未启动" : device?.State switch
        {
            PlcDeviceState.Connected => "", PlcDeviceState.Connecting => "连接中",
            PlcDeviceState.Faulted => "重连中", _ => "通讯断开"
        };
        // 即使位值没有变化，缓存/质量也可能变化，因此只读行更新必须在图形快照的相等判断之前。
        foreach (var item in PhysicalInputs.Concat(InterfaceRequests).Concat(PlcSignals)) item.Update(Field(item.Suffix), contextReason);
        var actions = Enumerable.Range(0, 9).Select(i => Field($"Action[{i}]")?.DisplayBooleanValue).ToArray();
        var signals = Enumerable.Range(0, 6).Select(i => Field($"Signal[{i}]")?.DisplayBooleanValue).ToArray();
        bool canDebug = UserAccessRules.CanEditParameters(_session.CurrentUser);
        var canWrite = _active && _visible && Fresh() && _access.CanControlModes && _writer.Enabled && _runtime.Status.State == PlcRuntimeState.Running &&
            _runtime.Status.ConfigurationVersion == _stamp && _runtime.Status.Devices.Any(d => d.DeviceId == _device && d.State == PlcDeviceState.Connected && d.ConnectionEpoch == _epoch) &&
            new[] { "Request.Debug", "Request.Initialize", "Request.Maintenance" }.All(s => Field(s)?.Binding is { DataType: PlcDataType.Bool } b && b.Access.HasFlag(PlcAccess.Write));
        bool canStop = _active && _visible && _ready && _access.CanControlModes && _writer.Enabled &&
            _runtime.Status.State == PlcRuntimeState.Running && _runtime.Status.ConfigurationVersion == _stamp &&
            device?.State == PlcDeviceState.Connected && device.ConnectionEpoch == _epoch &&
            new[] { "Mode", "Status", "Action[3]" }.All(s => Field(s)?.HasCurrentValue == true) &&
            Field("Action[3]")?.Binding is { DataType: PlcDataType.Bool, WriteMode: "握手请求" } stopBinding && stopBinding.Access.HasFlag(PlcAccess.Write);
        // 可点击条件的解释独立于状态颜色，避免手动/停止时用户仍不知道是哪项条件阻止请求。
        ModeOperationHint = BuildOperationHint(canWrite, device);
        bool canFollow = _active && _visible && _observing && _ready &&
            _runtime.Status.State == PlcRuntimeState.Running && _runtime.Status.ConfigurationVersion == _stamp &&
            device?.State == PlcDeviceState.Connected && device.ConnectionEpoch == _epoch &&
            Field("Mode")?.HasCurrentValue == true && Field("Status")?.HasCurrentValue == true &&
            _status is 0 or 10 or 20 or 30 or 40 or 50 or 60 or 70;
        ConfirmedDiagram = !canFollow ? null : _status == 70 ? "Emergency" :
            _mode switch { 0 => "Manual", 10 => "Maintenance", 20 => "Automatic", _ => null };
        // 先设置目标图，再发布对应的模式/状态，视图回调不会混用前后两次反馈。
        ConfirmedState = ConfirmedDiagram is not null && _mode is { } mode && _status is { } status ? (mode, status) : null;
        if (Display.Mode == _mode && Display.Status == _status && Display.CanWrite == canWrite && Display.CanStop == canStop && Display.CanDebug == canDebug && Display.Actions.SequenceEqual(actions) && Display.Signals.SequenceEqual(signals)) return;
        Display = new(_mode, _status, actions, signals, canWrite) { CanDebug = canDebug, CanStop = canStop };
        this.RaisePropertyChanged(nameof(CanToggleDebug));
        this.RaisePropertyChanged(nameof(CanRequestInitialization));
        foreach (var name in new[] { nameof(ModeText), nameof(StatusText), nameof(KnobText), nameof(RunText), nameof(PauseText), nameof(StopText), nameof(ResetText), nameof(EmergencyText), nameof(InitializationText), nameof(PauseSignalText), nameof(FaultSignalText) }) this.RaisePropertyChanged(name);
    }

    private string BuildOperationHint(bool canWrite, PlcDeviceRuntimeStatus? device)
    {
        if (MaintenanceInterlock.Blocked) return "维护锁定中：设备操作和参数下发已禁用；人员锁管理仍可用于继续上锁或验证解锁。";
        if (!_access.IsLoggedIn) return "尚未登录：可查看状态，登录后才可请求调试和初始化。";
        if (!_writer.Enabled) return "PLC 写入开关未启用，当前只读。";
        if (_runtime.Status.State != PlcRuntimeState.Running || device?.State != PlcDeviceState.Connected) return "PLC 未连接或采集未运行：保留上次状态，请等待当前连接有效读取。";
        if (!Fresh()) return "等待当前连接的有效模式、状态及命令反馈；缓存值不能用于请求操作。";
        if (!_access.CanControlModes) return "当前账户没有模式操作权限。";
        if (!canWrite) return "写入配置或当前连接条件尚未就绪，请核对模式命令的启用状态和写入绑定。";
        if (!UserAccessRules.CanEditParameters(_session.CurrentUser)) return "操作员不能进入调试或打开参数设置；需设备工程师、工艺工程师或管理员登录。初始化仍按模式、状态及急停条件判断。";
        bool exiting = Field("Action[6]")?.BooleanValue == true;
        if (Field("Signal[5]")?.BooleanValue != false)
            return "调试、初始化暂不可用：急停信号 b_Signal[5]=True；需解除急停并读取到 False。" + (exiting ? " 已置位的调试请求仍可通过调试开关退出。" : "");
        if (IsInitializationHeld) return "初始化按钮按住中：松开后向原设备发送 False；切页、失焦或退出登录也尝试复位。";
        if (_mode != 0) return "设备实际处于" + ModeName(_mode) + "；进入调试和请求初始化需现场切至手动并停止。上方页签仅切换查看。";
        if (_status != 0) return exiting ? "调试请求已置位，可通过调试开关退出；新的初始化请求需设备回到停止状态。" : "当前为" + StatusName(_status) + "；进入调试和请求初始化需设备停止。";
        if (Field("Action[7]")?.BooleanValue == true) return "初始化请求仍为 True，等待当前有效反馈确认清零后才可再次按下。";
        return "实际手动模式、停止状态且急停信号未触发：可操作调试开关和初始化。PLC 确认进入调试后开放参数设置；上方页签仅切换查看。";
    }

    /// <summary>
    /// 每次点击都重新核对角色、当前连接/配置、页面代次和有效反馈。只发送一次确定的布尔值，
    /// 不自动重试，仅停止位3按用户确认允许写入，不写 i_Mode/i_Status，不通过状态枚举完成模式切换。
    /// </summary>
    public async Task ExecuteAsync(string action)
    {
        if (action is "Initialize" or "Stop") { OperationMessage = "初始化和停止均采用按住操作：按下置位，松开复位。"; return; }
        await ExecuteCoreAsync(action);
    }

    public async Task PressInitializationAsync()
    {
        if (_writing || IsStopHeld || IsInitializationHeld || _initializationTarget is not null) return;
        var press = new CancellationTokenSource();
        _initializationPress = press; SetInitializationHeld(true);
        await ExecuteCoreAsync("Initialize", press.Token);
    }

    public async Task PressStopAsync()
    {
        if (IsStopHeld || _stopTarget is not null) return;
        // 第一次 await 前登记按下。等待初始化释放期间若已松开，不能随后补发 True。
        var press = new CancellationTokenSource();
        var token = press.Token;
        _stopPress = press; IsStopHeld = true;
        await ReleaseInitializationAsync();
        if (token.IsCancellationRequested || !IsStopHeld) return;
        if (_writing) { await ReleaseStopAsync(); return; }
        await ExecuteCoreAsync("Stop", token);
    }

    public async Task ReleaseMomentaryRequestsAsync()
    {
        await ReleaseStopAsync();
        await ReleaseInitializationAsync();
    }

    private async Task ExecuteCoreAsync(string action, CancellationToken pressToken = default)
    {
        if (action.StartsWith("Maintenance", StringComparison.Ordinal)) { OperationMessage = "维护进入和退出请使用人员锁管理，由每位人员验证本人身份。"; return; }
        if (_writing) { OperationMessage = "上一项模式操作尚未结束。"; return; }
        var actor = OperationAudit.CaptureActor(_session);
        var audit = new OperationAudit.Attempt(actor, "Mode" + action, _device, Component, "本机模式控制");
        if (action != "Stop" && IsStopHeld) { OperationMessage = "请先松开停止按钮。"; return; }
        if (action != "Initialize" && IsInitializationHeld) { OperationMessage = "请先松开初始化按钮。"; return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_target.Token, pressToken);
        var token = linked.Token; var revision = _revision; var authorization = _access.Version;
        bool initialized = false;
        _writing = true;
        try
        {
            await _operationGate.WaitAsync(token);
            try
            {
                RefreshSnapshots();
                if (action.StartsWith("Debug", StringComparison.Ordinal) && !UserAccessRules.CanEditParameters(_session.CurrentUser))
                    throw new UnauthorizedAccessException("操作员不能进入调试，需设备工程师、工艺工程师或管理员登录。");
                if (revision != _revision || authorization != _access.Version || !_access.CanControlModes || !Display.TryRequest(action, out var index, out var value))
                    throw new InvalidOperationException("页面、权限或当前反馈不满足该操作，请确认最新状态。");
                var suffix = index switch { 3 => "Action[3]", 6 => "Request.Debug", 7 => "Request.Initialize", 8 => "Request.Maintenance", _ => throw new InvalidOperationException() };
                var command = Field(suffix) ?? throw new InvalidOperationException("缺少当前操作的写入绑定。");
                if (!command.Binding.Access.HasFlag(PlcAccess.Write) || command.Binding.DataType != PlcDataType.Bool)
                    throw new InvalidOperationException("操作绑定未启用写权限或数据类型不匹配。");
                var context = _writer.Capture(_device!);
                if (context.ConnectionEpoch != _epoch || context.ConfigurationStamp != _stamp) throw new InvalidOperationException("连接或配置已改变，请重新读取后操作。");
                token.ThrowIfCancellationRequested();
                // 发送前固定原目标；置位结果未知时，释放仍尝试清零该原连接，不能从新页面借用目标。
                if (index == 7) _initializationTarget = (context, command.Key, actor);
                if (index == 3) _stopTarget = (context, command.Key, actor);
                if (action == "DebugEnter")
                    BeginDebugSession(context, command.Key, actor);
                PlcVariableWrite[] values = [new(command.Key, value ? "True" : "False")];
                audit.Prepare(context, [values], _data);
                // 审计准备后再次检查授权，避免慢目录读取期间退出登录后继续提交。
                if (authorization != _access.Version || !_access.CanControlModes) throw new UnauthorizedAccessException("登录权限已变化。");
                var result = await _writer.WriteAsync(context, values, cancellationToken: token);
                if (action == "DebugExit" && result.Success)
                {
                    Interlocked.Exchange(ref _debugFeedbackAfterTicks, DateTimeOffset.UtcNow.UtcTicks);
                    _debugOwner = null;
                    Field("Action[6]")?.ShowAcceptedBoolean(false, context.ConnectionEpoch, context.ConfigurationStamp);
                    DebugParameterAccess.Revoke(); Publish();
                }
                initialized = result.Success && !token.IsCancellationRequested;
                audit.Complete(result, token.IsCancellationRequested);
                if (revision == _revision && authorization == _access.Version && !token.IsCancellationRequested)
                {
                    if (result.Success)
                    {
                        Field($"Action[{index}]")?.ShowAcceptedBoolean(value, context.ConnectionEpoch, context.ConfigurationStamp);
                        Publish(); OperationMessage = "写入成功；实际模式和状态以 PLC 后续读取为准。";
                    }
                    else OperationMessage = "模式操作写入未确认：" + string.Join("；", result.Results.Select(r => r.Message).Distinct());
                }
            }
            finally { _operationGate.Release(); }
        }
        catch (Exception ex) { audit.Fail(ex); if (revision == _revision) OperationMessage = ex is OperationCanceledException ? "操作已取消。" : ex.Message; Logs.LogError(ex); }
        finally
        {
            _writing = false;
            if (action == "Initialize" && !initialized) await ReleaseInitializationAsync();
            if (action == "Stop" && !initialized) await ReleaseStopAsync();
            await OperationAudit.WriteAsync([audit]);
        }
    }

    /// <summary>调试由进入时的人员及连接持有；退出登录/降级/切换账号时撤销访问资格并向原目标写 False。
    /// 不借用新账号授权、不使用已取消的页面令牌，不跨重连补发；失败明确提示并保留审计。</summary>
    private async Task ReleaseDebugOnIdentityChangeAsync()
    {
        DebugParameterAccess.Revoke();
        OperationAudit.Attempt? audit = null;
        await _operationGate.WaitAsync();
        try
        {
            var owner = _debugOwner; _debugOwner = null;
            if (owner is null) return;
            audit = new(owner.Value.Actor, "ModeDebugLogoutExit", owner.Value.Key.DeviceId, Component, "参数调试退出");
            PlcVariableWrite[] values = [new(owner.Value.Key, "False")];
            audit.Prepare(owner.Value.Context, [values], null);
            var report = await _writer.WriteAsync(owner.Value.Context, values);
            audit.Complete(report, false);
            if (report.Success)
            {
                Interlocked.Exchange(ref _debugFeedbackAfterTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (_device == owner.Value.Context.DeviceId && _epoch == owner.Value.Context.ConnectionEpoch && _stamp == owner.Value.Context.ConfigurationStamp)
                { Field("Action[6]")?.ShowAcceptedBoolean(false, _epoch, _stamp!); Publish(); }
                OperationMessage = "身份已退出或改变，调试请求已清除；实际状态以 PLC 反馈为准。";
            }
            else OperationMessage = "参数权限已撤销，但自动退出调试写入未确认；请核对 PLC，不跨重连补发。";
        }
        catch (Exception ex) { audit?.Fail(ex); OperationMessage = "参数权限已撤销，但退出调试未确认：" + ex.Message; Logs.LogError(ex); }
        finally { _operationGate.Release(); if (audit is not null) await OperationAudit.WriteAsync([audit]); }
    }

    /// <summary>释放独立于新动作授权：不因离页、退出登录或维护锁而丢掉清零。
    /// 使用原始连接上下文；断线或配置更换时写入服务拒绝旧代次，绝不跨重连补发。</summary>
    public async Task ReleaseInitializationAsync()
    {
        var press = Interlocked.Exchange(ref _initializationPress, null);
        try { press?.Cancel(); } catch (ObjectDisposedException) { }
        SetInitializationHeld(false);
        OperationAudit.Attempt? audit = null;
        await _operationGate.WaitAsync();
        try
        {
            var target = _initializationTarget; _initializationTarget = null;
            if (target is null) return;
            audit = new(target.Value.Actor, "ModeInitializeRelease", target.Value.Key.DeviceId, Component, "本机模式控制");
            PlcVariableWrite[] values = [new(target.Value.Key, "False")];
            audit.Prepare(target.Value.Context, [values], null);
            var report = await _writer.WriteAsync(target.Value.Context, values);
            audit.Complete(report, false);
            if (report.Success)
            {
                if (_device == target.Value.Context.DeviceId && _epoch == target.Value.Context.ConnectionEpoch && _stamp == target.Value.Context.ConfigurationStamp)
                { Field("Action[7]")?.ShowAcceptedBoolean(false, _epoch, _stamp!); Publish(); }
                OperationMessage = "初始化松开复位写入已确认，实际状态以 PLC 读取为准。";
            }
            else OperationMessage = "初始化松开复位未确认：" + string.Join("；", report.Results.Select(r => r.Message).Distinct()) + "；不跨重连补发，请核对 PLC。";
        }
        catch (Exception ex) { audit?.Fail(ex); OperationMessage = "初始化松开复位未确认：" + ex.Message; Logs.LogError(ex); }
        finally { _operationGate.Release(); press?.Dispose(); if (audit is not null) await OperationAudit.WriteAsync([audit]); }
    }

    /// <summary>松开停止使用按下时的原连接、配置及操作者；身份变化、离页和失焦也释放，不跨重连补发。</summary>
    public async Task ReleaseStopAsync()
    {
        var press = Interlocked.Exchange(ref _stopPress, null);
        try { press?.Cancel(); } catch (ObjectDisposedException) { }
        IsStopHeld = false;
        OperationAudit.Attempt? audit = null;
        await _operationGate.WaitAsync();
        try
        {
            var target = _stopTarget; _stopTarget = null;
            if (target is null) return;
            audit = new(target.Value.Actor, "ModeStopRelease", target.Value.Key.DeviceId, Component, "本机模式控制");
            PlcVariableWrite[] values = [new(target.Value.Key, "False")];
            audit.Prepare(target.Value.Context, [values], null);
            var report = await _writer.WriteAsync(target.Value.Context, values);
            audit.Complete(report, false);
            if (report.Success)
            {
                if (_device == target.Value.Context.DeviceId && _epoch == target.Value.Context.ConnectionEpoch && _stamp == target.Value.Context.ConfigurationStamp)
                { Field("Action[3]")?.ShowAcceptedBoolean(false, _epoch, _stamp!); Publish(); }
                OperationMessage = "停止松开复位写入已确认，实际状态以 PLC 读取为准。";
            }
            else OperationMessage = "停止松开复位未确认：" + string.Join("；", report.Results.Select(r => r.Message).Distinct()) + "；不跨重连补发，请核对 PLC。";
        }
        catch (Exception ex) { audit?.Fail(ex); OperationMessage = "停止松开复位未确认：" + ex.Message; Logs.LogError(ex); }
        finally { _operationGate.Release(); press?.Dispose(); if (audit is not null) await OperationAudit.WriteAsync([audit]); }
    }

    /// <summary>维护凭据仅用于这次验证，不建立新 HMI 会话、不记住密码。
    /// 先持久化人员锁，再写约定的 b_Action[8]；失败及结果未知都保留记录，由本人核对后重试。</summary>
    public async Task MaintainAsync(string userName, string password, int? unlockId, bool administratorAll = false)
    {
        await ReleaseMomentaryRequestsAsync();
        if (_writing) throw new InvalidOperationException("上一项操作尚未结束。");
        _writing = true;
        OperationAudit.Attempt? audit = null;
        await _operationGate.WaitAsync();
        try
        {
            var revision = _revision;
            var user = await _accounts.AuthenticateAsync(userName, password) ?? throw new UnauthorizedAccessException("用户名或密码错误。");
            if (administratorAll && user.Role != (int)UserRole.Administrator) throw new UnauthorizedAccessException("解除全部维护锁必须验证管理员账户。");
            if (unlockId is { } owner && user.Id != owner) throw new UnauthorizedAccessException("必须使用上锁者本人的账户验证解锁。");
            if (!Enum.IsDefined((UserRole)user.Role)) throw new UnauthorizedAccessException("账户类别无效。");
            RefreshSnapshots();
            if (!_active || !_visible || revision != _revision || !Fresh() || _device is null)
                throw new InvalidOperationException("请等待当前模式、状态及维护请求的有效 PLC 反馈后操作。");
            var context = _writer.Capture(_device);
            if (context.ConnectionEpoch != _epoch || context.ConfigurationStamp != _stamp) throw new InvalidOperationException("连接或配置发生变化，请重新读取。");
            bool releasing = unlockId.HasValue || administratorAll;
            // 已持锁人员可重试尚未确认的进入请求；不得生成第二把同名锁。
            bool needsWrite;
            var description = administratorAll ? "管理员解除全部维护锁：" + string.Join("、", MaintenanceInterlock.People.Select(p => p.UserName)) : "维护人员锁";
            audit = new(new(user.Id, user.UserName, user.Role), administratorAll ? "MaintenanceAdminUnlockAll" : releasing ? "MaintenanceUnlock" : "MaintenanceLock", _device, Component, description);
            if (administratorAll) { await Task.Run(() => MaintenanceInterlock.ReleaseAll(user)); needsWrite = true; }
            else if (releasing) needsWrite = await Task.Run(() => MaintenanceInterlock.Release(user));
            else if (MaintenanceInterlock.Holds(user.Id))
            {
                if (MaintenanceInterlock.People.Any(p => p.Releasing)) throw new InvalidOperationException("退出维护尚待确认，请通过本人解锁入口核对或重试。");
                needsWrite = true;
            }
            else
            {
                // 仅第一把锁需要检查进入维护的前提；已经处于维护时增加人员锁只更新人员记录，
                // 不因现场急停信号变化阻止其他人员登记，也不重复触发 PLC 维护请求。
                bool first = MaintenanceInterlock.People.Length == 0;
                if (first && !(_status is 0 or 10 or 20))
                    throw new InvalidOperationException($"首次上锁未执行：当前状态为 {StatusText}。请先使设备处于停止、故障或暂停状态，再重新上锁。");
                // 用户确认：急停回路断开也允许维护人员上锁；此处不改变调试、初始化等动作的急停判断。
                needsWrite = await Task.Run(() => MaintenanceInterlock.Add(user));
            }
            if (needsWrite)
            {
                var command = Field("Request.Maintenance") ?? throw new InvalidOperationException("缺少维护写入绑定。");
                PlcVariableWrite[] values = [new(command.Key, releasing ? "False" : "True")];
                audit.Prepare(context, [values], _data);
                var report = await _writer.WriteAsync(context, values);
                audit.Complete(report, false);
                if (!report.Success) throw new InvalidOperationException("维护请求未确认，人员锁记录保留。请核对通讯和 PLC 反馈，由本人重试。");
            }
            else audit.CompleteLocal();
            OperationMessage = releasing ? "本人解锁已验证；其他锁仍保留，最后一人退出需要 PLC 确认。" : "本人上锁已记录，维护状态以 PLC 反馈为准。";
            if (_observing) { await _page.RefreshAsync(); RefreshSnapshots(); }
        }
        catch (Exception ex) { audit?.Fail(ex); OperationMessage = ex.Message; throw; }
        finally { _operationGate.Release(); _writing = false; if (audit is not null) await OperationAudit.WriteAsync([audit]); }
    }

    public void OnNavigatedTo(NavigationContext context) { _active = true; ObserveNavigation(); }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { _active = false; ObserveNavigation(); }
    private async void ObserveNavigation() { try { await ObserveAsync(); } catch (Exception ex) { Message = ex.Message; Logs.LogError(ex); } }
}

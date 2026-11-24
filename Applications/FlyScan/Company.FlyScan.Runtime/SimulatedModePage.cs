using Company.Hmi.Contracts;
using System.ComponentModel;

namespace Company.FlyScan.Runtime;

public sealed class SimulatedModePage : IModeControlPage
{
    private readonly SimulatedMachine _machine;
    private bool _visible, _held;
    public SimulatedModePage(SimulatedMachine machine)
    {
        _machine = machine;
        _machine.PropertyChanged += (_, _) => { _held = false; Publish(); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public (int Mode, int Status)? ConfirmedState => _machine.Mode is int mode && _machine.Status is int status ? (mode, status) : null;
    public string? ConfirmedDiagram => ConfirmedState switch
    {
        { Status: 70 } => "Emergency",
        { Mode: 0 } => "Manual",
        { Mode: 10 } => "Maintenance",
        { Mode: 20 } => "Automatic",
        _ => null
    };
    public IModeDiagramFeedback Feedback => Display;
    public IModeDiagramFeedback Display => new Diagram(_machine.Mode, _machine.Status, _machine.Initialized, _held,
        CanToggleDebug, CanRequestInitialization, _visible && _machine.CanRequest("Stop"));
    public string ModeText => SimulatedMachine.Modes.FirstOrDefault(x => x.Code == _machine.Mode)?.Name ?? "未知";
    public string StatusText => SimulatedMachine.States.FirstOrDefault(x => x.Code == _machine.Status)?.Name ?? "未知";
    public string FeedbackHeading => "运动卡仿真反馈";
    public string RunExplanation => "模拟反馈由仿真面板提供。停止、调试及初始化请求由飞拍仿真处理。";
    public string MaintenanceExplanation => "维护状态期间禁止轴调试及参数修改。";
    public string RecoveryExplanation => "未知反馈或急停时禁止新操作。恢复反馈后按当前模式重新判断权限。";
    public string Message => ConfirmedState is null ? "仿真反馈未知。" : "正在查看飞拍仿真运行状态。";
    public string OperationMessage { get; private set; } = "";
    public string ModeOperationHint => CanToggleDebug ? "手动模式下可请求调试或退出调试。" : "调试需要对应角色和有效手动反馈。";
    public bool CanToggleDebug => _visible && (_machine.CanRequest("DebugEnter") || _machine.CanRequest("DebugExit"));
    public bool CanRequestInitialization => _visible && _machine.CanRequest("Initialize");
    public bool CanAdminUnlock => false;
    public IReadOnlyList<object> MaintenancePeople => [];
    public string MaintenanceSummary => "";
    public IReadOnlyList<Row> InterfaceRequests =>
    [
        Row.From("调试请求", "Action[6]", Display.Action(6)),
        Row.From("初始化请求", "Action[7]", Display.Action(7))
    ];
    public IReadOnlyList<Row> PhysicalInputs => [Row.From("手自动旋钮", "Action[0]", Display.Action(0))];
    public IReadOnlyList<Row> PlcSignals =>
    [
        Row.From("初始化完成", "Signal[0]", Display.Signal(0)),
        Row.From("暂停反馈", "Signal[1]", Display.Signal(1)),
        Row.From("告警反馈", "Signal[2]", Display.Signal(2)),
        Row.From("急停反馈", "Signal[5]", Display.Signal(5))
    ];

    public Task SetVisibleAsync(bool visible)
    {
        if (visible == _visible) return Task.CompletedTask;
        _visible = visible;
        if (!visible) _held = false;
        Publish();
        return Task.CompletedTask;
    }
    public Task ExecuteAsync(string action)
    {
        OperationMessage = _visible ? _machine.RequestModeAction(action) : "模式控制页面未显示。";
        Publish();
        return Task.CompletedTask;
    }
    public Task PressStopAsync() => ExecuteAsync("Stop");
    public Task ReleaseStopAsync() => Task.CompletedTask;
    public async Task PressInitializationAsync()
    {
        bool accepted = CanRequestInitialization;
        await ExecuteAsync("Initialize");
        _held = accepted && _machine.Status == 40;
        Publish();
    }
    public Task ReleaseInitializationAsync()
    {
        _held = false;
        Publish();
        return Task.CompletedTask;
    }
    public Task ReleaseMomentaryRequestsAsync() => ReleaseInitializationAsync();

    private void Publish() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public sealed record Row(string Name, string Suffix, string ValueText, bool IsOn, string Details) : IModeRequestRow
    {
        public static Row From(string name, string suffix, bool? value) =>
            new(name, suffix, value is null ? "未知" : value.Value ? "已触发" : "未触发", value == true, "飞拍仿真反馈");
    }

    private sealed record Diagram(int? Mode, int? Status, bool Initialized, bool Held, bool Debug, bool Initialize,
        bool Stop) : IModeDiagramFeedback
    {
        public bool SupportsPersonnelLocks => false;
        public bool? Action(int index) => Mode is null ? null : index switch
        {
            0 => Mode == 20, 6 => Status == 30, 7 => Held, _ => null
        };
        public bool? Signal(int index) => Status is null ? null : index switch
        {
            0 => Initialized, 1 => Status == 20, 2 => Status == 10, 5 => Status == 70, _ => null
        };
        public bool CanRequest(string action) => action switch
        {
            "DebugEnter" => Debug && Status == 0, "DebugExit" => Debug && Status == 30,
            "Initialize" => Initialize, "Stop" => Stop, _ => false
        };
    }
}

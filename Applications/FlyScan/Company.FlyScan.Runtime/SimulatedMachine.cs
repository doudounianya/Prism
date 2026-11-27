using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using System.ComponentModel;

namespace Company.FlyScan.Runtime;

/// <summary>本机 G2 仿真运行入口。命令与模拟反馈在宿主调度线程串行执行。</summary>
public sealed partial class SimulatedMachine : INotifyPropertyChanged, IDisposable
{
    private readonly NoneControlCard _card;
    private CurrentUser _user = new();
    private bool _connected = true, _confirmed = true, _initialized;
    private int _mode, _status;
    private bool _disposed;
    private AxisId? _joggedAxis;

    public static IReadOnlyList<SimulationRole> Roles { get; } =
    [
        new("访客", null), new("操作员", UserRole.Operator), new("设备工程师", UserRole.EquipmentEngineer),
        new("工艺工程师", UserRole.ProcessEngineer), new("管理员", UserRole.Administrator)
    ];
    public static IReadOnlyList<SimulationMode> Modes { get; } =
    [
        new(0, "手动模式"), new(20, "自动模式"), new(10, "维护模式")
    ];
    public static IReadOnlyList<SimulationMode> States { get; } =
    [
        new(0, "停止"), new(10, "故障"), new(20, "暂停"), new(30, "调试"),
        new(40, "初始化"), new(50, "运行"), new(60, "维护任务"), new(70, "急停")
    ];

    public SimulatedMachine(string profilePath)
    {
        _card = NoneControlCard.CreateForSimulation(_clock);
        var profile = MotionConfigurationFile.Load(profilePath);
        profile.Enabled = true;
        profile.AllowNativeHardware = false;
        if (!_card.Initialize(profile))
            throw new InvalidOperationException("无法启动飞拍运动卡仿真。");
        Axes = Array.AsReadOnly(profile.Axes);
        Coordinates = Array.AsReadOnly(profile.Coordinates);
        Synchronizations = Array.AsReadOnly(profile.Synchronizations);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<AxisConfiguration> Axes { get; }
    public IReadOnlyList<CoordinateConfiguration> Coordinates { get; }
    public IReadOnlyList<SynchronizationConfiguration> Synchronizations { get; }
    public bool CardConnected => !_disposed && _connected;
    public int? Mode => CardConnected && _confirmed ? _mode : null;
    public int? Status => CardConnected && _confirmed ? _status : null;
    public long ContextVersion { get; private set; }
    public bool Initialized => _initialized;
    public bool IsLoggedIn => RoleAccessRules.IsLoggedIn(_user);
    public string Actor => Roles.FirstOrDefault(item => (int?)item.Role == _user.Role)?.Name ?? "访客";
    public bool CanConfigure => RoleAccessRules.CanConfigureEquipment(_user);
    public bool CanAdjust => !_disposed && _scanOwner is null && _card.IsInitialized && CanConfigure && Mode == 0 && Status == 30;

    public void SelectRole(UserRole? role)
    {
        if ((_user.Id == 0 && role is null) || (_user.Id > 0 && (int?)role == _user.Role)) return;
        _user = role is null ? new() : new() { Id = 1, UserName = "仿真账号", Role = (int)role };
        InvalidateAccess();
    }

    /// <summary>测试入口模拟控制端报告的状态。普通页面浏览页签不调用它。</summary>
    public void SetFeedback(int mode, int status)
    {
        if (!Modes.Any(x => x.Code == mode) || !States.Any(x => x.Code == status))
            throw new ArgumentOutOfRangeException(nameof(status), "未知仿真模式或状态。");
        if (_confirmed && _mode == mode && _status == status) return;
        _confirmed = true;
        _mode = mode;
        _status = status;
        if (status == 40) _initialized = false;
        InvalidateAccess();
    }

    public void LoseFeedback()
    {
        if (!_confirmed) return;
        _confirmed = false;
        InvalidateAccess();
    }

    public void SetCardConnected(bool connected)
    {
        if (_disposed || _connected == connected) return;
        _connected = connected;
        InvalidateAccess();
    }

    public void CompleteInitialization()
    {
        if (Mode != 0 || Status != 40) return;
        _initialized = true;
        SetFeedback(0, 0);
    }

    public bool IsFollower(AxisId id) => Synchronizations.Any(x => x.Enabled && x.SlaveAxis == id);

    public AxisState? ReadAxis(AxisId id)
    {
        if (!CardConnected || !_confirmed || !_card.RefreshAxisState(id)) return null;
        return _card.AxisStates.FirstOrDefault(state => state.Axis == id && state.Valid);
    }

    public bool CanOperate(AxisId axis)
    {
        var state = ReadAxis(axis);
        return CanAdjust && !IsFollower(axis) && state is { EmergencyStop: false, Mapped: true };
    }

    private void InvalidateAccess()
    {
        ++ContextVersion;
        ReleaseJog();
        _card.TryStop(null, Status == 70 ? StopMode.Immediate : StopMode.Decelerate, out _);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public string RequestModeAction(string action)
    {
        if (!CanRequest(action)) return "当前仿真反馈或角色不允许操作。";
        switch (action)
        {
            case "DebugEnter":
                SetFeedback(0, 30);
                break;
            case "DebugExit":
                SetFeedback(0, 0);
                break;
            case "Initialize":
                SetFeedback(0, 40);
                break;
            case "Stop":
                if (!_card.TryStop(null, StopMode.Decelerate, out var explanation)) return explanation;
                SetFeedback(_mode, 0);
                break;
        }
        return "仿真状态已更新。";
    }

    public bool CanRequest(string action) => !_disposed && IsLoggedIn && action switch
    {
        "DebugEnter" => CanConfigure && Mode == 0 && Status == 0,
        "DebugExit" => CanConfigure && Mode == 0 && Status == 30,
        "Initialize" => Mode == 0 && Status == 0,
        "Stop" => (Mode, Status) is (0, 40) or (20, 50),
        _ => false
    };

    public bool TryOperateAxis(AxisId id, string action, out string message, double position = 0)
    {
        if (!CanOperate(id))
        {
            message = "需手动调试、有效反馈和对应角色；同步从轴不可独立操作。";
            return false;
        }
        if (_joggedAxis is not null && action != "Stop")
        {
            message = "请先松开点动。";
            return false;
        }
        var state = ReadAxis(id)!;
        string details;
        bool accepted;
        switch (action)
        {
            case "Enable": accepted = _card.SetAxisEnabled(id, !state.Enable, out details); break;
            case "Home": accepted = _card.HomeAxis(id, out details); break;
            case "Reset": accepted = _card.ResetAxisFault(id, out details); break;
            case "Stop": ReleaseJog(); accepted = _card.TryStop(id, StopMode.Decelerate, out details); break;
            case "Absolute": accepted = _card.MoveAbsolute(id, position, out details); break;
            case "Relative": accepted = _card.MoveRelative(id, position, out details); break;
            case "Positive":
            case "Negative":
                accepted = _card.JogAxis(id, action == "Positive" ? JogDirection.Positive : JogDirection.Negative, out details);
                if (accepted) _joggedAxis = id;
                break;
            default: message = "未知轴操作。"; return false;
        }
        message = accepted ? "仿真请求已接受，以运动卡仿真反馈为准。" : details;
        return accepted;
    }

    public void ReleaseJog()
    {
        if (_joggedAxis is not { } axis) return;
        if (_card.TryStop(axis, StopMode.Decelerate, out _)) _joggedAxis = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanOwner = null;
        ReleaseJog();
        _card.Close();
    }
}

public sealed record SimulationRole(string Name, UserRole? Role);
public sealed record SimulationMode(int Code, string Name);

using Company.Hardware.ControlCard;
using Company.Hmi.Contracts;
using System.ComponentModel;
using System.Globalization;

namespace Company.FlyScan.Runtime;

public sealed class SimulatedAxisPage : IAxisParameterPage, INotifyPropertyChanged
{
    private readonly SimulatedMachine _machine;
    private readonly Dictionary<AxisId, (PositionRow Absolute, PositionRow Relative)> _positions;
    private readonly ParameterField _enabled = new(), _home = new(), _negativeLimit = new(), _positiveLimit = new(), _origin = new();
    private readonly ParameterField _actualPosition = new(), _actualVelocity = new(), _target = new(), _alarm = new(), _stop = new(), _idleSwitch = new();
    private bool _visible;
    private long _selectionVersion;
    private AxisOption? _selected;
    private AxisState? _feedback;
    private PositionRow? _activePosition;
    private int _selectedTab = 3;
    private IReadOnlyList<ManualParameter> _manualParameters = [];

    public SimulatedAxisPage(SimulatedMachine machine)
    {
        _machine = machine;
        Components = machine.Axes.Select(x => new AxisOption(x)).ToArray();
        _positions = Components.ToDictionary(option => option.Axis.Axis, option =>
            (new PositionRow(option, false), new PositionRow(option, true)));
        _selected = Components.FirstOrDefault();
        LoadManualParameters();
        _machine.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Devices { get; } = ["IMC30G-E 仿真"];
    public string SelectedDevice { get => Devices[0]; set { } }
    public IReadOnlyList<AxisOption> Components { get; }
    public AxisOption? SelectedComponent
    {
        get => _selected;
        set
        {
            if (value == _selected || value is null || !Components.Contains(value)) return;
            _machine.ReleaseJog();
            ++_selectionVersion;
            _selected = value;
            LoadManualParameters();
            Refresh();
            foreach (string property in new[] { nameof(SelectedComponent), nameof(PositionUnit), nameof(SpeedUnit),
                nameof(AccelerationUnit), nameof(DecelerationUnit), nameof(AbsolutePositions),
                nameof(RelativePositions), nameof(ManualParameters) })
                Publish(property);
        }
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is not (3 or 4) || value == _selectedTab) return;
            ++_selectionVersion;
            _selectedTab = value;
            Publish(nameof(SelectedTab));
            Publish(nameof(PositionColumnTitle));
        }
    }
    public bool CanUseControls => _visible && !_machine.IsFollower(Axis.Axis) && _machine.CanAdjust &&
        _feedback is { Mapped: true, EmergencyStop: false };
    public bool CanOperate => CanUseControls;
    public string WriteModeText => _machine.IsFollower(Axis.Axis) ? "同步从轴 · 仅反馈" :
        CanUseControls ? "手动调试 · 仿真操作" : "只读查看";
    public string OperationMessage { get; private set; } = "";
    public string PositionColumnTitle => SelectedTab == 4 ? "相对位置" : "绝对位置";
    private AxisConfiguration Axis => SelectedComponent!.Axis;
    public string PositionUnit => Axis.Unit == MotionAxisUnit.Degree ? "°" : "mm";
    public string SpeedUnit => PositionUnit + "/s";
    public string AccelerationUnit => PositionUnit + "/s²";
    public string DecelerationUnit => AccelerationUnit;

    public ParameterField EnableState => _enabled;
    public ParameterField MotorState => _enabled;
    public ParameterField HomeState => _home;
    public ParameterField OriginState => _home;
    public ParameterField ErrorCode => _alarm;
    public ParameterField StopState => _stop;
    public ParameterField NegativeState => _idleSwitch;
    public ParameterField PositiveState => _idleSwitch;
    public ParameterField ResetState => _idleSwitch;
    public ParameterField NegativeLimit => _negativeLimit;
    public ParameterField PositiveLimit => _positiveLimit;
    public ParameterField OriginSensor => _origin;
    public ParameterField? ProbeState => null;
    public ParameterField ActualPosition => _actualPosition;
    public ParameterField ActualVelocity => _actualVelocity;
    public ParameterField? ActualTorque => null;
    public ParameterField TargetPosition => _target;
    public IReadOnlyList<PositionRow> AbsolutePositions => [_positions[Axis.Axis].Absolute];
    public IReadOnlyList<PositionRow> RelativePositions => [_positions[Axis.Axis].Relative];
    public IReadOnlyList<ManualParameter> ManualParameters => _manualParameters;
    private void LoadManualParameters() => _manualParameters =
    [
        new("Jog 速度", Axis.JogVelocity, SpeedUnit),
        new("停止减速度", Axis.JogDeceleration, DecelerationUnit),
        new("点动加速度", Axis.JogAcceleration, AccelerationUnit),
        new("点动减速度", Axis.JogDeceleration, DecelerationUnit)
    ];

    public Task SetViewVisibleAsync(bool visible)
    {
        if (_visible == visible) return Task.CompletedTask;
        _visible = visible;
        ++_selectionVersion;
        if (!visible) _machine.ReleaseJog();
        Refresh();
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        if (_visible) Refresh();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        _feedback = _visible ? _machine.ReadAxis(Axis.Axis) : null;
        _enabled.Set(_feedback?.Enable);
        _home.Set(_feedback?.Homed);
        _negativeLimit.Set(_feedback?.NegativeLimit);
        _positiveLimit.Set(_feedback?.PositiveLimit);
        _origin.Set(_feedback is { OriginKnown: true } ? _feedback.Origin : null);
        _alarm.Set(_feedback?.FaultCode);
        _actualPosition.Set(_feedback?.ActualPosition, PositionUnit);
        _actualVelocity.Set(_feedback?.ActualVelocity, SpeedUnit);
        _target.Set(_feedback?.CommandPosition, PositionUnit);
        _stop.Set(_feedback?.Busy);
        _idleSwitch.Set(_feedback is null ? null : false);
        var rows = _positions[Axis.Axis];
        UpdatePosition(rows.Absolute);
        UpdatePosition(rows.Relative);
        // 行和字段实例长期复用。快照轮询只更新发生变化的字段。
        bool canOperate = CanOperate;
        string newMode = WriteModeText;
        if (_lastCanOperate != canOperate || _lastWriteMode != newMode)
        {
            _lastCanOperate = canOperate;
            _lastWriteMode = newMode;
            Publish(nameof(CanOperate));
            Publish(nameof(CanUseControls));
            Publish(nameof(WriteModeText));
        }
    }

    private bool _lastCanOperate;
    private string? _lastWriteMode;
    private void UpdatePosition(PositionRow row)
    {
        row.RunState.Set(_feedback is null ? null : _activePosition == row && _feedback.Busy);
        row.DoneState.Set(_activePosition == row ? _feedback?.Arrived : null);
    }
    private void Publish(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Task OperateAsync(string action, int? position = null)
    {
        string message = "当前条件不允许轴操作。";
        if (CanOperate) _machine.TryOperateAxis(Axis.Axis, action, out message);
        OperationMessage = message;
        Publish(nameof(OperationMessage));
        Refresh();
        return Task.CompletedTask;
    }
    public Task PressMomentaryAsync(string action) => OperateAsync(action);
    public Task ReleaseJogAsync() { _machine.ReleaseJog(); Refresh(); return Task.CompletedTask; }

    public Task MoveAsync(object row)
    {
        if (row is not PositionRow target || !CurrentRow(target) || !CanOperate) return Task.CompletedTask;
        bool stop = target.RunState.BooleanValue == true;
        bool accepted = _machine.TryOperateAxis(Axis.Axis,
            stop ? "Stop" : target.IsRelative ? "Relative" : "Absolute", out var message, target.Target);
        OperationMessage = message;
        if (!stop && accepted) _activePosition = target;
        Publish(nameof(OperationMessage));
        Refresh();
        return Task.CompletedTask;
    }

    public ParameterEntry? BeginEntry(object field)
    {
        if (!CanOperate || _feedback?.Busy != false || !_machine.CanOperate(Axis.Axis)) return null;
        var row = SelectedTab == 3 ? AbsolutePositions[0] : RelativePositions[0];
        if (!ReferenceEquals(row.Position, field)) return null;
        long permission = _machine.ContextVersion;
        long selection = _selectionVersion;
        string? Validate(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed) ? null : "请输入有效的有限数值。";
        return new ParameterEntry(row.Name, PositionUnit, row.Position.Edit.Draft, false, Validate, value =>
        {
            if (permission != _machine.ContextVersion || selection != _selectionVersion || !CanOperate ||
                !CurrentRow(row) || _machine.ReadAxis(Axis.Axis) is not { Busy: false } ||
                !_machine.CanOperate(Axis.Axis) || Validate(value) is not null)
                OperationMessage = "上下文已变化或输入无效，参数未保存。";
            else
            {
                row.Target = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                row.Position.Set(row.Target, PositionUnit);
                OperationMessage = "已保存本次仿真定位目标。";
            }
            Publish(nameof(OperationMessage));
            return Task.CompletedTask;
        });
    }

    private bool CurrentRow(PositionRow row) =>
        row.Axis == SelectedComponent && row.IsRelative == (SelectedTab == 4);

    public sealed record AxisOption(AxisConfiguration Axis)
    {
        public string Name => $"{Axis.Name} · {Axis.Axis} · {Axis.Bus}[{Axis.HardwareAxis}]";
        public override string ToString() => Name;
    }

    public sealed class PositionRow
    {
        public PositionRow(AxisOption axis, bool relative)
        {
            Axis = axis;
            IsRelative = relative;
            Target = relative ? 0 : axis.Axis.DefaultPosition;
            var unit = axis.Axis.Unit == MotionAxisUnit.Degree ? "°" : "mm";
            Position.Set(Target, unit);
            Speed.Set(axis.Axis.PositioningVelocity, unit + "/s");
            Acceleration.Set(axis.Axis.PositioningAcceleration, unit + "/s²");
            Deceleration.Set(axis.Axis.PositioningDeceleration, unit + "/s²");
        }
        public AxisOption Axis { get; }
        public bool IsRelative { get; }
        public int Index => 0;
        public string Name => IsRelative ? "相对定位测试点" : "绝对定位测试点";
        public double Target { get; internal set; }
        public ParameterField Position { get; } = new();
        public ParameterField Speed { get; } = new();
        public ParameterField Acceleration { get; } = new();
        public ParameterField Deceleration { get; } = new();
        public ParameterField? Dwell => null;
        public ParameterField RunState { get; } = new();
        public ParameterField DoneState { get; } = new();
    }
    public sealed record ManualParameter(string Name, double Parameter, string Unit)
    {
        public string Label => $"{Name}（{Unit}）";
        public ParameterField Field { get; } = CreateField(Parameter, Unit);
        private static ParameterField CreateField(double value, string unit)
        {
            var field = new ParameterField();
            field.Set(value, unit);
            return field;
        }
    }

    public sealed class ParameterField : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Value { get; private set; } = "—";
        public string Unit { get; private set; } = "";
        public string State => Value == "—" ? "无有效反馈" : "运动卡仿真";
        public ParameterDraft Edit { get; private set; } = new("—");
        public bool? BooleanValue { get; private set; }
        public bool? DisplayBooleanValue => BooleanValue;
        public void Set(object? value, string unit = "")
        {
            var display = value is IFormattable number
                ? number.ToString(value is double ? "0.###" : null, CultureInfo.InvariantCulture)
                : value?.ToString() ?? "—";
            if (Value == display && Unit == unit && BooleanValue == value as bool?) return;
            Value = display;
            Unit = unit;
            BooleanValue = value as bool?;
            Edit = new ParameterDraft(display);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
    public sealed record ParameterDraft(string Draft)
    {
        public string Result => "";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Company.Hardware.ControlCard.None;

/// <summary>
/// 完全离线的控制卡模型，不调用厂商 SDK。位置按时间和有限速度推进，读取状态时完成一次仿真步进。
/// 此模型验证命令/状态/生命周期，不代表执行器惯量、伺服加减速曲线或安全回路的物理模型。
/// </summary>
public sealed class NoneControlCard : ControlCardBase
{
    private enum Motion { Idle, Position, Home, Jog, Coordinate }
    private sealed class AxisSimulation
    {
        internal double Position;
        internal double Target;
        internal double Velocity;
        internal double Speed;
        internal int JogDirection;
        internal Motion Motion;
        internal bool Enabled;
        internal bool Paused;
        internal bool Homed;
        internal bool Arrived = true;
        internal bool Alarm;
        internal bool EmergencyStop;
        internal bool PositiveLimit;
        internal bool NegativeLimit;
        internal CrdType? Coordinate;
    }
    private sealed class CoordinateSimulation
    {
        internal required CrdType Type;
        internal required AxisType[] Axes;
        internal readonly List<PosXYZ> Points = [];
        internal int Point;
        internal double Speed;
        internal bool Active;
        internal bool Paused;
    }

    private readonly TimeProvider _clock;
    private readonly Dictionary<AxisType, AxisSimulation> _axes = [];
    private readonly Dictionary<CrdType, CoordinateSimulation> _coordinates = [];
    private DateTimeOffset _lastTick;
    private bool _open;

    public NoneControlCard() : this(TimeProvider.System) { }
    private NoneControlCard(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    /// <summary>离线工装使用受控时钟，无需 Sleep 或连接实际设备。</summary>
    public static NoneControlCard CreateForSimulation(TimeProvider clock) => new(clock);
    protected override DateTimeOffset CurrentTime => _clock.GetUtcNow();

    protected override bool DoInit()
    {
        _axes.Clear(); _coordinates.Clear();
        foreach (var axis in AxisTypes) _axes[axis] = new AxisSimulation();
        foreach (var config in Config.AxisThreeConfigs)
            _coordinates[config.CrdType] = new CoordinateSimulation { Type = config.CrdType, Axes = [config.Axis_X, config.Axis_Y, config.Axis_Z] };
        _lastTick = CurrentTime;
        _open = true;
        return true;
    }

    protected override bool DoConfigure() => _open;
    protected override void DoClose()
    {
        _open = false;
        _axes.Clear(); _coordinates.Clear();
    }

    private AxisSingleConfig Parameters(AxisType axis) => Config.AxisSingleConfigs.Single(x => x.Axis == axis);
    private bool Exists(AxisType axis) => _open && _axes.ContainsKey(axis);
    private static bool Running(AxisSimulation axis) => axis.Motion != Motion.Idle && !axis.Paused;

    /// <summary>先推进共享坐标，再推进独立轴；每一轴每次时间差只被消费一次。</summary>
    private void Advance()
    {
        if (!_open) return;
        var now = CurrentTime;
        var seconds = Math.Max(0, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        foreach (var coordinate in _coordinates.Values)
            if (coordinate.Active && !coordinate.Paused) AdvanceCoordinate(coordinate, seconds);
        foreach (var pair in _axes)
        {
            var axis = pair.Value;
            if (axis.Motion is Motion.Idle or Motion.Coordinate || axis.Paused) continue;
            if (!axis.Enabled || axis.Alarm || axis.EmergencyStop) { Halt(axis); continue; }
            double direction = axis.Motion == Motion.Jog ? axis.JogDirection : Math.Sign(axis.Target - axis.Position);
            if (direction > 0 && axis.PositiveLimit || direction < 0 && axis.NegativeLimit) { Halt(axis); continue; }
            var config = Parameters(pair.Key);
            double next = axis.Position + direction * axis.Speed * seconds;
            bool completed = axis.Motion != Motion.Jog && (direction == 0 || direction > 0 && next >= axis.Target || direction < 0 && next <= axis.Target);
            if (completed) next = axis.Target;
            if (config.SoftLimitEnabled && (next > config.SoftLimitPositive || next < config.SoftLimitNegative))
            {
                axis.Position = Math.Clamp(next, config.SoftLimitNegative, config.SoftLimitPositive);
                Halt(axis); axis.Arrived = false; continue;
            }
            axis.Position = next;
            axis.Velocity = direction * axis.Speed;
            if (completed)
            {
                bool homing = axis.Motion == Motion.Home;
                Halt(axis); axis.Arrived = true;
                if (homing) axis.Homed = true;
            }
        }
        Publish(now);
    }

    private void AdvanceCoordinate(CoordinateSimulation coordinate, double seconds)
    {
        if (coordinate.Axes.Any(x => !_axes.TryGetValue(x, out var state) || !state.Enabled || state.Alarm || state.EmergencyStop))
        { CancelCoordinate(coordinate); return; }
        while (coordinate.Active)
        {
            var point = coordinate.Points[coordinate.Point];
            double[] targets = [point.Pos_X, point.Pos_Y, point.Pos_Z];
            var deltas = coordinate.Axes.Select((axis, index) => targets[index] - _axes[axis].Position).ToArray();
            double distance = Math.Sqrt(deltas.Sum(x => x * x));
            if (!double.IsFinite(distance)) { CancelCoordinate(coordinate); return; }
            for (int i = 0; i < coordinate.Axes.Length; i++)
            {
                var state = _axes[coordinate.Axes[i]];
                var config = Parameters(coordinate.Axes[i]);
                if (state.PositiveLimit && deltas[i] > 0 || state.NegativeLimit && deltas[i] < 0 ||
                    config.SoftLimitEnabled && (targets[i] < config.SoftLimitNegative || targets[i] > config.SoftLimitPositive))
                { CancelCoordinate(coordinate); return; }
            }
            double needed = distance / coordinate.Speed;
            bool complete = distance <= 1e-12 || seconds >= needed;
            double ratio = complete ? 1 : coordinate.Speed * seconds / distance;
            for (int i = 0; i < coordinate.Axes.Length; i++)
            {
                var state = _axes[coordinate.Axes[i]];
                state.Position += deltas[i] * ratio;
                state.Velocity = distance > 1e-12 ? coordinate.Speed * deltas[i] / distance : 0;
            }
            if (!complete) break;
            seconds = Math.Max(0, seconds - needed);
            coordinate.Point++;
            if (coordinate.Point >= coordinate.Points.Count)
            {
                coordinate.Active = false; coordinate.Paused = false;
                foreach (var axis in coordinate.Axes) { Halt(_axes[axis]); _axes[axis].Arrived = true; }
            }
            else if (seconds <= 0) break;
        }
    }

    private static void Halt(AxisSimulation axis)
    {
        axis.Motion = Motion.Idle; axis.Paused = false; axis.Velocity = 0; axis.Coordinate = null;
    }

    private void CancelCoordinate(CoordinateSimulation coordinate)
    {
        coordinate.Active = false; coordinate.Paused = false; coordinate.Points.Clear(); coordinate.Point = 0;
        foreach (var axis in coordinate.Axes)
            if (_axes.TryGetValue(axis, out var state) && state.Coordinate == coordinate.Type) { Halt(state); state.Arrived = false; }
    }

    private void StopAxis(AxisType axis)
    {
        var state = _axes[axis];
        if (state.Coordinate.HasValue && _coordinates.TryGetValue(state.Coordinate.Value, out var coordinate)) CancelCoordinate(coordinate);
        else { Halt(state); state.Arrived = false; }
    }

    /// <summary>RawStatus 是仿真位图，仅用于观察，不能解释为任一厂商原始状态字。</summary>
    private void Publish(DateTimeOffset now)
    {
        foreach (var state in AxisStates)
        {
            if (!_axes.TryGetValue(state.Axis, out var axis)) { state.Valid = false; continue; }
            state.Enable = axis.Enabled; state.Busy = Running(axis); state.Paused = axis.Paused;
            state.Alarm = axis.Alarm; state.EmergencyStop = axis.EmergencyStop;
            state.PositiveLimit = axis.PositiveLimit; state.NegativeLimit = axis.NegativeLimit;
            state.Origin = Math.Abs(axis.Position) <= 1e-6; state.Homed = axis.Homed; state.Arrived = axis.Arrived;
            state.PrfPos = axis.Position; state.EncfPos = axis.Position;
            state.PrfVel = state.EncVel = state.Busy ? axis.Velocity : 0;
            state.PrfAcc = state.EncAcc = 0;
            state.RawStatus = (state.Enable ? 1 : 0) | (state.Busy ? 2 : 0) | (state.Alarm ? 4 : 0) |
                (state.Arrived ? 8 : 0) | (state.Homed ? 16 : 0) | (state.Paused ? 32 : 0) |
                (state.PositiveLimit ? 64 : 0) | (state.NegativeLimit ? 128 : 0) | (state.EmergencyStop ? 256 : 0);
            state.Valid = true; state.UpdatedAt = now;
        }
        foreach (var state in CrdStates)
        {
            state.Exist = _coordinates.TryGetValue(state.CrdType, out var coordinate);
            state.Valid = _open; state.UpdatedAt = now;
            state.Busy = coordinate is not null && coordinate.Active && !coordinate.Paused;
            state.Paused = coordinate?.Paused ?? false;
            state.Alarm = coordinate is not null && coordinate.Axes.Any(x => _axes[x].Alarm || _axes[x].EmergencyStop);
            state.RawStatus = (state.Exist ? 1 : 0) | (state.Busy ? 2 : 0) | (state.Paused ? 4 : 0) | (state.Alarm ? 8 : 0);
        }
    }

    protected override bool DoGetAxisStatus(AxisType? axisType)
    {
        if (!_open || axisType.HasValue && !Exists(axisType.Value)) return false;
        Advance(); return true;
    }

    protected override bool DoGetCrdStatus(CrdType? crdType)
    {
        if (!_open || crdType.HasValue && !_coordinates.ContainsKey(crdType.Value)) return false;
        Advance(); return true;
    }

    protected override bool DoSetAxisEnable(AxisType? axisType, bool value)
    {
        Advance();
        if (!_open || axisType.HasValue && !Exists(axisType.Value)) return false;
        var axes = axisType.HasValue ? new[] { axisType.Value } : _axes.Keys.ToArray();
        if (value && axes.Any(x => _axes[x].Alarm || _axes[x].EmergencyStop)) return false;
        foreach (var axis in axes)
        {
            if (!value) StopAxis(axis);
            _axes[axis].Enabled = value;
        }
        Publish(CurrentTime); return true;
    }

    private bool StartPosition(AxisType axisType, double target, double speed, bool home = false)
    {
        Advance();
        if (!Exists(axisType) || !double.IsFinite(target) || !double.IsFinite(speed) || speed <= 0) return false;
        var axis = _axes[axisType]; var config = Parameters(axisType);
        if (!axis.Enabled || axis.Alarm || axis.EmergencyStop || axis.Motion != Motion.Idle || axis.Paused) return false;
        if (config.SoftLimitEnabled && (target < config.SoftLimitNegative || target > config.SoftLimitPositive)) return false;
        if (!home && (axis.PositiveLimit && target > axis.Position || axis.NegativeLimit && target < axis.Position)) return false;
        axis.Target = target; axis.Speed = speed; axis.Velocity = Math.Sign(target - axis.Position) * speed;
        axis.Motion = home ? Motion.Home : Motion.Position; axis.Paused = false; axis.Arrived = false;
        if (home) axis.Homed = false;
        Publish(CurrentTime); return true;
    }

    protected override bool DoGoHome(AxisType axisType, AxisSingleConfig config) => StartPosition(axisType, 0, config.HomehighVel, home: true);
    protected override bool DoPtpSingleMoveTo(AxisType axisType, double pos, AxisSingleConfig config) => StartPosition(axisType, pos, config.PtpVel);
    protected override bool DoPtpSingleMoveBy(AxisType axisType, double pos, AxisSingleConfig config)
    {
        Advance(); return Exists(axisType) && StartPosition(axisType, _axes[axisType].Position + pos, config.PtpVel);
    }

    protected override bool DoJogSingleMove(AxisType axisType, MoveDirection direction, AxisSingleConfig config)
    {
        Advance();
        if (!Exists(axisType) || !Enum.IsDefined(direction)) return false;
        var axis = _axes[axisType];
        if (!axis.Enabled || axis.Alarm || axis.EmergencyStop || axis.Motion != Motion.Idle || axis.Paused) return false;
        bool positive = direction == MoveDirection.正向;
        if (positive && axis.PositiveLimit || !positive && axis.NegativeLimit) return false;
        axis.Motion = Motion.Jog; axis.Speed = config.JogVel;
        axis.JogDirection = positive ? 1 : -1;
        axis.Velocity = positive ? axis.Speed : -axis.Speed; axis.Arrived = false;
        Publish(CurrentTime); return true;
    }

    protected override bool DoPtpSinglePause(AxisType axisType)
    {
        Advance();
        if (!Exists(axisType)) return false;
        var axis = _axes[axisType];
        if (!Running(axis) || axis.Motion == Motion.Coordinate) return false;
        axis.Paused = true; axis.Velocity = 0; Publish(CurrentTime); return true;
    }

    protected override bool DoPtpSingleResume(AxisType axisType)
    {
        Advance();
        if (!Exists(axisType)) return false;
        var axis = _axes[axisType];
        if (!axis.Paused || axis.Motion is Motion.Idle or Motion.Coordinate || !axis.Enabled || axis.Alarm || axis.EmergencyStop) return false;
        axis.Paused = false;
        axis.Velocity = axis.Motion == Motion.Jog ? axis.JogDirection * axis.Speed : Math.Sign(axis.Target - axis.Position) * axis.Speed;
        Publish(CurrentTime); return true;
    }

    protected override void DoStop(AxisType? axisType, AxisStopMode mode)
    {
        Advance();
        if (!_open) throw new InvalidOperationException("仿真控制卡已关闭。");
        if (!Enum.IsDefined(mode) || axisType.HasValue && !Exists(axisType.Value)) throw new ArgumentException("轴或停止模式无效。");
        foreach (var axis in axisType.HasValue ? new[] { axisType.Value } : _axes.Keys.ToArray()) StopAxis(axis);
        // 仿真不计算机械惯性；两种停止均在当前模拟位置结束，并保留明确停止类型的API覆盖。
        Publish(CurrentTime);
    }

    protected override bool DoResetAxisFault(AxisType? axisType)
    {
        Advance();
        if (!_open || axisType.HasValue && !Exists(axisType.Value)) return false;
        var axes = axisType.HasValue ? new[] { axisType.Value } : _axes.Keys.ToArray();
        if (axes.Any(x => Running(_axes[x]) || _axes[x].EmergencyStop)) return false;
        foreach (var axis in axes) _axes[axis].Alarm = false;
        Publish(CurrentTime); return true;
    }

    protected override bool DoCrdLineMoveTo_XYZ(CrdType type, List<PosXYZ> points, AxisThreeConfig config)
    {
        Advance();
        if (!_open || !_coordinates.TryGetValue(type, out var coordinate) || coordinate.Active || points.Count == 0) return false;
        AxisType[] axes = [config.Axis_X, config.Axis_Y, config.Axis_Z];
        if (axes.Distinct().Count() != 3 || axes.Any(x => !Exists(x) || _axes[x].Motion != Motion.Idle || !_axes[x].Enabled || _axes[x].Alarm || _axes[x].EmergencyStop)) return false;
        if (_coordinates.Values.Any(x => x.Active && x.Axes.Intersect(axes).Any())) return false;
        coordinate.Axes = axes;
        coordinate.Points.Clear(); coordinate.Points.AddRange(points.Select(x => new PosXYZ { Pos_X = x.Pos_X, Pos_Y = x.Pos_Y, Pos_Z = x.Pos_Z }));
        coordinate.Point = 0; coordinate.Speed = config.CrdLineVel; coordinate.Active = true; coordinate.Paused = false;
        foreach (var axis in axes)
        { _axes[axis].Motion = Motion.Coordinate; _axes[axis].Coordinate = type; _axes[axis].Arrived = false; }
        Publish(CurrentTime); return true;
    }

    protected override bool DoCrdPause(CrdType type)
    {
        Advance();
        if (!_open || !_coordinates.TryGetValue(type, out var coordinate) || !coordinate.Active || coordinate.Paused) return false;
        coordinate.Paused = true;
        foreach (var axis in coordinate.Axes) { _axes[axis].Paused = true; _axes[axis].Velocity = 0; }
        Publish(CurrentTime); return true;
    }

    protected override bool DoCrdResume(CrdType type)
    {
        Advance();
        if (!_open || !_coordinates.TryGetValue(type, out var coordinate) || !coordinate.Active || !coordinate.Paused) return false;
        if (coordinate.Axes.Any(x => !_axes[x].Enabled || _axes[x].Alarm || _axes[x].EmergencyStop)) return false;
        coordinate.Paused = false;
        foreach (var axis in coordinate.Axes) _axes[axis].Paused = false;
        Publish(CurrentTime); return true;
    }

    protected override bool DoCrdClr(CrdType type)
    {
        Advance();
        if (!_open || !_coordinates.TryGetValue(type, out var coordinate) || coordinate.Active && !coordinate.Paused) return false;
        CancelCoordinate(coordinate); Publish(CurrentTime); return true;
    }

    protected override bool DoInputGetStatus()
    {
        if (!_open) return false;
        foreach (var state in InputStates) { state.Valid = true; state.UpdatedAt = CurrentTime; }
        return true;
    }

    protected override bool DoOutputGetStatus()
    {
        if (!_open) return false;
        foreach (var state in OutputStates) { state.Valid = true; state.UpdatedAt = CurrentTime; }
        return true;
    }

    protected override bool DoOutputSetStatus(BitStatus bit, bool value)
    {
        if (!_open) return false;
        var state = OutputStates.SingleOrDefault(x => x.Board == bit.Board && x.Port == bit.Port);
        if (state is null) return false;
        state.Value = value; state.Valid = true; state.UpdatedAt = CurrentTime; return true;
    }

    /// <summary>仅用于本仿真对象的离线输入注入；未初始化或越界地址返回 false。</summary>
    public bool SetSimulatedInput(uint group, int bit, bool value) => Synchronize(() =>
    {
        if (!_open || bit is < 0 or >= 16) return false;
        var state = InputStates.SingleOrDefault(x => x.Board == group && x.Port == bit);
        if (state is null) return false;
        state.Value = value; state.Valid = true; state.UpdatedAt = CurrentTime; return true;
    });

    /// <summary>离线注入报警、急停和硬件限位；触发报警/急停即停止相关运动，复位不能清除仍存在的急停。</summary>
    public bool SetSimulatedFault(AxisType axisType, bool alarm, bool emergencyStop = false,
        bool positiveLimit = false, bool negativeLimit = false) => Synchronize(() =>
    {
        Advance();
        if (!Exists(axisType)) return false;
        var axis = _axes[axisType];
        axis.Alarm = alarm; axis.EmergencyStop = emergencyStop;
        axis.PositiveLimit = positiveLimit; axis.NegativeLimit = negativeLimit;
        if (alarm || emergencyStop) StopAxis(axisType);
        Publish(CurrentTime); return true;
    });
}

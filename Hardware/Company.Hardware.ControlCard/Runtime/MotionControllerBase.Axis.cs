using System.Diagnostics;

namespace Company.Hardware.ControlCard;

/// <summary>单轴状态、准入校验、运动及等待。与其它分文件共用同一会话、串行锁和待确认状态。</summary>
public abstract partial class MotionControllerBase
{
    private AxisState State(AxisId axis)
    {
        Require(axis.IsValid && AxisIds.Contains(axis), "轴不属于当前配置。");
        return AxisStateBuffer.Single(x => x.Axis == axis);
    }

    private AxisConfiguration AxisConfig(AxisId axis) => Configuration.Axes.Single(x => x.Axis == axis);

    private AxisState RefreshAxis(AxisId axis)
    {
        var state = State(axis);
        try
        {
            Require(ReadAxisStatesCore(axis), "轴状态读取失败。");
            Require(state.Valid, "轴状态无效，不能依据旧数据操作。");
            if (CoordinateOwner(axis) is CoordinateId coordinate) RefreshCoordinate(coordinate);
            ObservePending(state);
            return state;
        }
        catch { state.Valid = false; _ready = false; throw; }
    }

    private AxisState MotionAxis(AxisId axis, bool allowPaused = false, CoordinateId? allowedOwner = null)
    {
        Require(!Configuration.Synchronizations.Any(s => s.Enabled && s.SlaveAxis == axis), "同步从轴不能独立运动，请操作其主轴。");
        var state = RefreshAxis(axis);
        foreach (var relation in Configuration.Synchronizations.Where(s => s.Enabled && s.MasterAxis == axis))
        {
            var slave = RefreshAxis(relation.SlaveAxis);
            Require(slave.Valid && slave.Mapped && slave.Enable && !slave.Alarm && !slave.EmergencyStop && !slave.Homing,
                "同步从轴未就绪、报警或急停，不能启动主轴。");
            Require(!slave.Busy && (allowPaused || !slave.Paused), "同步从轴仍在运动或暂停中。");
            Require(CoordinateOwner(slave.Axis) is null || CoordinateOwner(slave.Axis) == allowedOwner, "同步从轴仍由其他轨迹占用。");
        }
        Require(state.Mapped, "轴尚未确认物理映射，不能使用虚轴默认状态操作。");
        var owner = CoordinateOwner(axis);
        Require(owner is null || owner == allowedOwner, "轴仍由轨迹任务占用，请完成或明确取消该任务。");
        Require(!state.Homing, "轴仍处于回零或回零退出确认中。");
        Require(state.Enable, "轴未使能。");
        Require(!state.Alarm && !state.EmergencyStop, "轴报警或急停触发。");
        Require(!_pending.ContainsKey(axis), "上一运动尚未获得完成反馈，请等待或明确停止后重试。");
        Require(!state.Busy && (allowPaused || !state.Paused), "轴正在运动或暂停中，请先停止。");
        return state;
    }

    /// <summary>
    /// 新命令接受后不能凭扫描延迟期间的 Busy=false 再发动作。
    /// 观察过 Busy 再回空闲，或新鲜反馈明确到达本次目标/回零，才解除待确认标记。
    /// 极短动作若反馈无法确认，标记保守保留，超时不自动授权下一动作；停止成功可释放它。
    /// </summary>
    private void ObservePending(AxisState state)
    {
        if (!_pending.TryGetValue(state.Axis, out var pending)) return;
        if (state.Busy) { pending.ObservedBusy = true; return; }
        if (state.Paused) return;
        // 本方法只在本会话一次同步状态读取成功后进入；不能依赖UTC先后顺序，系统校时可能回拨。
        bool atTarget = pending.Target.HasValue && state.Arrived && double.IsFinite(state.ActualPosition) &&
            Math.Abs(state.ActualPosition - pending.Target.Value) <= 1e-6;
        bool completed = pending.Home
            ? state.Homed && !state.Homing && !state.Alarm && !state.EmergencyStop
            : pending.ObservedBusy || atTarget;
        if (completed) _pending.Remove(state.Axis);
    }

    private PendingMotion MarkPending(AxisId axis, double? target = null, bool home = false, CoordinateId? coordinate = null)
    {
        var pending = new PendingMotion(target, home, coordinate);
        _pending[axis] = pending;
        return pending;
    }

    /// <summary>
    /// 明确不支持表示驱动没有提交动作，只撤销本次建立的标记；其它异常不能证明未下发，保守保留。
    /// 引用身份比较避免清除另一操作或其它轴原有的待确认状态。
    /// </summary>
    private bool SubmitPending(Func<bool> action, params (AxisId Axis, PendingMotion Pending)[] created)
    {
        void RemoveCreated()
        {
            foreach (var item in created)
                if (_pending.TryGetValue(item.Axis, out var current) && ReferenceEquals(current, item.Pending))
                    _pending.Remove(item.Axis);
        }
        try
        {
            bool accepted = action();
            if (!accepted) RemoveCreated();
            return accepted;
        }
        catch (NotSupportedException) { RemoveCreated(); throw; }
    }

    private static void CheckAxisTarget(AxisState state, AxisConfiguration config, double target)
    {
        Require(double.IsFinite(target) && double.IsFinite(state.ActualPosition), "位置必须是有限数值。");
        if (config.SoftLimitEnabled)
            Require(target >= config.SoftLimitNegative && target <= config.SoftLimitPositive, "目标位置超出软件限位。");
        // 触发限位后只允许离开该限位的方向，不能因任一限位触发就堵死退回动作。
        Require(!(state.PositiveLimit && target > state.ActualPosition) && !(state.NegativeLimit && target < state.ActualPosition),
            "目标方向上的硬件限位已触发。");
    }

    private void CheckTarget(AxisState state, AxisConfiguration config, double target)
    {
        CheckAxisTarget(state, config, target);
        foreach (var relation in Configuration.Synchronizations.Where(s => s.Enabled && s.MasterAxis == state.Axis))
        {
            var slave = State(relation.SlaveAxis);
            CheckAxisTarget(slave, AxisConfig(slave.Axis), slave.ActualPosition + (target - state.ActualPosition) * relation.Ratio);
        }
    }

    protected AxisId[] CoupledAxes(AxisId? selected)
    {
        if (selected is null) return AxisIds.ToArray();
        var result = new HashSet<AxisId> { selected.Value };
        foreach (var relation in Configuration.Synchronizations.Where(s => s.Enabled))
            if (relation.MasterAxis == selected || relation.SlaveAxis == selected) result.Add(relation.MasterAxis);
        foreach (var relation in Configuration.Synchronizations.Where(s => s.Enabled))
            if (result.Contains(relation.MasterAxis)) result.Add(relation.SlaveAxis);
        return result.ToArray();
    }

    public bool RefreshAxisState(AxisId? axisId) => Execute("读取轴状态", () =>
    {
        if (axisId.HasValue) { RefreshAxis(axisId.Value); return true; }
        try
        {
            bool success = ReadAxisStatesCore(null) && AxisStateBuffer.All(x => x.Valid);
            if (success)
            {
                foreach (var coordinate in _coordinateClaims.Keys.ToArray()) RefreshCoordinate(coordinate);
                foreach (var state in AxisStateBuffer) ObservePending(state);
            }
            if (!success) foreach (var state in AxisStateBuffer) state.Valid = false;
            _ready = success;
            return success;
        }
        catch { foreach (var state in AxisStateBuffer) state.Valid = false; _ready = false; throw; }
    }, out _);

    public bool SetAxisEnabled(AxisId? axisId, bool enabled, out string message) => Execute(enabled ? "使能" : "关闭使能", () =>
    {
        var axes = CoupledAxes(axisId);
        foreach (var axis in axes)
        {
            State(axis);
            if (enabled)
            {
                var state = RefreshAxis(axis);
                Require(state.Mapped && CoordinateOwner(axis) is null && !state.Homing && !_pausedMotion.ContainsKey(axis),
                    "轴未映射或仍由运动任务占用，不能开启使能。");
                Require(!state.Busy && !state.Alarm && !state.EmergencyStop, "运动、报警或急停时不能开启使能。");
            }
        }
        // 关闭使能是释放操作，不因反馈读取故障而阻止尝试关闭当前有效会话。
        bool result = SetAxisEnabledCore(axisId, enabled);
        if (result) foreach (var axis in axes)
        {
            State(axis).Enable = enabled; State(axis).Paused = false;
            if (!enabled) { _pending.Remove(axis); _pausedMotion.Remove(axis); }
        }
        return result;
    }, out message);

    public bool ResetAxisFault(AxisId? axisId, out string message) => Execute("轴报警复位", () =>
    {
        foreach (var axis in CoupledAxes(axisId))
            Require(!RefreshAxis(axis).Busy, "轴运动时不能复位报警。");
        return ResetAxisFaultCore(axisId);
    }, out message);

    public bool HomeAxis(AxisId axisId, out string message) => Execute("回零启动", () =>
    {
        var state = MotionAxis(axisId);
        var pending = MarkPending(axisId, home: true);
        bool result = SubmitPending(() => StartHomingCore(axisId, AxisConfig(axisId)), (axisId, pending));
        if (result) { state.Homed = false; state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool MoveAbsolute(AxisId axisId, double position, out string message) => Execute("绝对定位启动", () =>
    {
        var state = MotionAxis(axisId); var config = AxisConfig(axisId);
        CheckTarget(state, config, position);
        var pending = MarkPending(axisId, position);
        bool result = SubmitPending(() => MoveAbsoluteCore(axisId, position, config), (axisId, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool MoveRelative(AxisId axisId, double distance, out string message) => Execute("相对定位启动", () =>
    {
        var state = MotionAxis(axisId); var config = AxisConfig(axisId);
        Require(double.IsFinite(distance), "相对位移必须为有限数值。");
        var target = state.ActualPosition + distance;
        CheckTarget(state, config, target);
        var pending = MarkPending(axisId, target);
        bool result = SubmitPending(() => MoveRelativeCore(axisId, distance, config), (axisId, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool PauseAxis(AxisId axisId, out string message) => Execute("单轴暂停", () =>
    {
        Require(!Configuration.Synchronizations.Any(s => s.Enabled && s.SlaveAxis == axisId), "同步从轴不能独立暂停。");
        var state = RefreshAxis(axisId);
        Require(CoordinateOwner(axisId) is null && !state.Homing, "此轴属于轨迹或回零任务，请使用任务对应的暂停/停止入口。");
        Require(state.Enable && state.Busy, "轴未使能或未运行，不能暂停。");
        bool result = PauseAxisCore(axisId);
        // SDK 没有暂停反馈位时，这仅记录本会话已接受暂停请求，不能解释为硬件确认。
        if (result)
        {
            state.Paused = true;
            if (_pending.Remove(axisId, out var motion)) _pausedMotion[axisId] = motion;
        }
        return result;
    }, out message);

    public bool ResumeAxis(AxisId axisId, out string message) => Execute("单轴恢复", () =>
    {
        var state = MotionAxis(axisId, allowPaused: true);
        Require(state.Paused, "本会话没有已接受的暂停请求。");
        bool result = ResumeAxisCore(axisId);
        if (result)
        {
            state.Paused = false;
            if (_pausedMotion.Remove(axisId, out var motion)) MarkPending(axisId, motion.Target, motion.Home, motion.Coordinate);
            else MarkPending(axisId);
        }
        return result;
    }, out message);

    public bool JogAxis(AxisId axisId, JogDirection direction, out string message) => Execute("点动启动", () =>
    {
        Require(Enum.IsDefined(direction), "点动方向无效。");
        var state = MotionAxis(axisId); var config = AxisConfig(axisId);
        bool positive = direction == JogDirection.Positive;
        Require(!(positive && state.PositiveLimit) && !(!positive && state.NegativeLimit), "点动方向限位已触发。");
        Require(double.IsFinite(state.ActualPosition), "当前位置无效。");
        if (config.SoftLimitEnabled)
            Require(positive ? state.ActualPosition < config.SoftLimitPositive : state.ActualPosition > config.SoftLimitNegative,
                "点动方向软件限位已触发。");
        var pending = MarkPending(axisId);
        bool result = SubmitPending(() => JogAxisCore(axisId, direction, config), (axisId, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool TryStop(AxisId? axisId, StopMode mode, out string message) => Execute("停止", () =>
    {
        Require(Enum.IsDefined(mode), "停止模式无效。");
        if (axisId.HasValue) State(axisId.Value);
        StopCore(axisId, mode);
        foreach (var state in AxisStateBuffer.Where(x => CoupledAxes(axisId).Contains(x.Axis)))
        { state.Paused = false; _pending.Remove(state.Axis); _pausedMotion.Remove(state.Axis); }
        if (!axisId.HasValue) foreach (var state in CoordinateStateBuffer) state.Paused = false;
        return true;
    }, out message);

    /// <summary>
    /// 有界等待“当前 SDK 轴状态为空闲”，每轮读取后立即释放锁，Stop/Close 不会被等待循环占用。
    /// 空闲不等同于任意定位/回零成功；刚接受命令而 Busy 尚未置位时也可能空闲，调用方应另核对 Arrived/Homed。
    /// 原生单次读取的超时由厂商 SDK 控制；本方法无法中止卡住的非托管函数。
    /// </summary>
    public bool WaitForAxisIdle(AxisId axisId, TimeSpan timeout, CancellationToken cancellationToken, out string message)
    {
        message = "等待轴空闲失败。";
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) { message = "等待超时须大于零且不超过五分钟。"; return false; }
        var watch = Stopwatch.StartNew();
        long? session = null;
        while (watch.Elapsed < timeout)
        {
            if (cancellationToken.IsCancellationRequested) { message = "等待轴空闲已取消。"; return false; }
            var remaining = timeout - watch.Elapsed;
            bool entered = false;
            int poll = 20;
            try
            {
                if (remaining <= TimeSpan.Zero) break;
                // 短时尝试进入，不在等待锁期间忽略取消请求，也不无限排在卡住的SDK后面。
                if (!Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(Math.Min(20, remaining.TotalMilliseconds)))) continue;
                entered = true;
                Require(_initialized && (!session.HasValue || session == _session), "控制卡会话已经变化。");
                session ??= _session;
                poll = Configuration.PollIntervalMs;
                var state = RefreshAxis(axisId);
                Require(!state.Alarm && !state.EmergencyStop, "等待期间轴报警或急停。");
                if (!state.Busy && !state.Homing && !state.Paused && !_pending.ContainsKey(axisId) &&
                    !_pausedMotion.ContainsKey(axisId) && CoordinateOwner(axisId) is null)
                { message = "已读取到轴空闲；请按操作类型另核对到位或回零完成。"; return true; }
            }
            catch (Exception exception) { message = exception.Message; return false; }
            finally { if (entered) Monitor.Exit(_sync); }
            remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            if (cancellationToken.WaitHandle.WaitOne((int)Math.Min(poll, Math.Ceiling(remaining.TotalMilliseconds))))
            { message = "等待轴空闲已取消。"; return false; }
        }
        message = "等待轴空闲超时；停止/完成未确认，待确认动作仍保持阻断。";
        return false;
    }

    // 厂商只覆盖本功能所需的扩展点；公共校验仍通过上方入口统一执行。
    protected abstract bool StartHomingCore(AxisId axisId, AxisConfiguration axisConfiguration);
    protected abstract void StopCore(AxisId? axisId, StopMode stopMode);
    protected abstract bool ReadAxisStatesCore(AxisId? axisId);
    protected abstract bool SetAxisEnabledCore(AxisId? axisId, bool value);
    /// <summary>只有明确实现报警复位的厂商才能覆盖；清状态不自动等同驱动报警复位。</summary>
    protected virtual bool ResetAxisFaultCore(AxisId? axisId) => false;
    protected abstract bool MoveAbsoluteCore(AxisId axisId, double pos, AxisConfiguration axisConfiguration);
    protected abstract bool MoveRelativeCore(AxisId axisId, double pos, AxisConfiguration axisConfiguration);
    protected abstract bool PauseAxisCore(AxisId axisId);
    protected abstract bool ResumeAxisCore(AxisId axisId);
    protected abstract bool JogAxisCore(AxisId axisId, JogDirection moveDirection, AxisConfiguration axisConfiguration);
}

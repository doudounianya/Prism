using Company.Logger;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Company.Hardware.ControlCard;

/// <summary>
/// 控制卡公共入口。每次原生调用都在同一把锁内执行；状态刷新和指令准入不可被另一个指令插入。
/// 驱动只更新受保护的内部状态，公开集合返回克隆，调用方不能修改门控依据。
/// bool 仅表示请求被接受；运动、回零和插补是否完成必须读取实际状态，不能从返回值推断。
/// </summary>
public abstract class ControlCardBase : IControlCard
{
    private readonly object _sync = new();
    private bool _initialized;
    private bool _ready;
    private long _session;
    // 初始化状态与资源所有权分离：关闭失败时仍持有资源，不能以重新Init覆盖旧句柄。
    private bool _resourcesMayExist;
    private sealed class PendingMotion(double? target, bool home, DateTimeOffset started, CrdType? coordinate)
    {
        internal readonly double? Target = target;
        internal readonly bool Home = home;
        internal readonly DateTimeOffset Started = started;
        internal readonly CrdType? Coordinate = coordinate;
        internal bool ObservedBusy;
    }
    private readonly Dictionary<AxisType, PendingMotion> _pending = [];
    private readonly Dictionary<AxisType, PendingMotion> _pausedMotion = [];

    public bool Initialized { get { lock (_sync) return _initialized; } }
    public bool IsReady { get { lock (_sync) return _ready; } }
    protected ControlCardConfig Config { get; private set; } = null!;
    public IControlCardConfigProvider? Provider { get; private set; }
    /// <summary>真实驱动覆盖为 true；仿真不会加载任何厂商库。</summary>
    protected virtual bool RequiresNativeHardware => false;
    /// <summary>实际驱动使用系统UTC；仿真可覆盖为受控时钟，反馈与待确认时间必须同源。</summary>
    protected virtual DateTimeOffset CurrentTime => DateTimeOffset.UtcNow;
    /// <summary>仅供仿真工装等受控状态注入，复用与SDK调用完全相同的串行边界。</summary>
    protected T Synchronize<T>(Func<T> action) { lock (_sync) return action(); }
    protected IEnumerable<AxisType> AxisTypes { get; } = Enum.GetValues<AxisType>();
    protected IEnumerable<CrdType> CrdTypes { get; } = Enum.GetValues<CrdType>();

    protected List<AxisStatus> AxisStates { get; } = [];
    protected List<CrdStatus> CrdStates { get; } = [];
    protected List<BitStatus> InputStates { get; } = [];
    protected List<BitStatus> OutputStates { get; } = [];

    public IReadOnlyList<AxisStatus> AxisStatuss { get { lock (_sync) return AxisStates.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<CrdStatus> CrdStatuss { get { lock (_sync) return CrdStates.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<BitStatus> DiStatuss { get { lock (_sync) return InputStates.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<BitStatus> DoStatuss { get { lock (_sync) return OutputStates.Select(x => x.Snapshot()).ToArray(); } }

    protected ControlCardBase() => ResetStates(null);

    /// <summary>
    /// 显式启用后才打开卡。配置在此克隆，不订阅运行中任意替换的可变配置；修改配置后应停止并重新初始化。
    /// 任一步失败都尝试关闭资源，不能遗留 Initialized=true 或把真卡失败替换成仿真成功。
    /// </summary>
    public bool Init(IControlCardConfigProvider provider)
    {
        lock (_sync)
        {
            if (_initialized) { Logs.LogWarning("控制卡已经初始化，请先关闭再修改配置。"); return false; }
            if (_resourcesMayExist && !TryReleaseResources())
            { Logs.LogWarning("上一次控制卡资源尚未释放，拒绝重新初始化。"); return false; }
            bool opening = false;
            try
            {
                ArgumentNullException.ThrowIfNull(provider);
                Config = provider.ControlCardConfig?.Snapshot() ?? throw new ArgumentException("控制卡配置为空。");
                ValidateConfig(Config);
                Require(Config.Enabled, "控制卡未启用。");
                Require(!RequiresNativeHardware || Config.AllowNativeHardware, "真实控制卡未授权启用。");
                Provider = provider;
                ResetStates(Config);
                opening = true;
                _resourcesMayExist = true;
                Require(DoInit(), "控制卡初始化失败。");
                Require(DoConfigure(), "控制卡参数配置失败。");
                // 首次真实读取成功才发布就绪；轴反馈未知的卡不能接受运动命令。
                Require(DoGetAxisStatus(null) && AxisStates.All(s => s.Valid), "控制卡首次轴状态读取失败。");
                _initialized = true;
                _ready = true;
                _session++;
                return true;
            }
            catch (Exception exception)
            {
                LogSafely(exception);
                if (opening) TryReleaseResources();
                InvalidateSession();
                return false;
            }
        }
    }

    /// <summary>停止失败仍尝试关闭；最终使旧会话、旧状态和等待者失效。</summary>
    public void Close()
    {
        lock (_sync)
        {
            try
            {
                if (_initialized)
                {
                    try { DoStop(null, AxisStopMode.减速停止); }
                    catch (Exception exception) { LogSafely(exception); }
                }
                if (_resourcesMayExist) TryReleaseResources();
            }
            finally { InvalidateSession(); }
        }
    }

    private bool TryReleaseResources()
    {
        try { DoClose(); _resourcesMayExist = false; return true; }
        catch (Exception exception) { LogSafely(exception); return false; }
    }

    private static void LogSafely(Exception exception)
    {
        // 资源释放和受控失败不能再被日志写入异常打断。
        try { Logs.LogError(exception); } catch { }
    }

    private void InvalidateSession()
    {
        _initialized = false;
        _ready = false;
        _session++;
        _pending.Clear(); _pausedMotion.Clear();
        foreach (var state in AxisStates) { state.Valid = false; state.Enable = false; state.Busy = false; state.Paused = false; }
        foreach (var state in CrdStates) { state.Valid = false; state.Busy = false; state.Paused = false; }
        foreach (var state in InputStates) state.Valid = false;
        foreach (var state in OutputStates) state.Valid = false;
        Provider = null;
        Config = null!;
    }

    private void ResetStates(ControlCardConfig? config)
    {
        AxisStates.Clear(); CrdStates.Clear(); InputStates.Clear(); OutputStates.Clear();
        _pending.Clear(); _pausedMotion.Clear();
        foreach (var axis in AxisTypes) AxisStates.Add(new AxisStatus { Axis = axis });
        foreach (var coordinate in CrdTypes) CrdStates.Add(new CrdStatus { CrdType = coordinate });
        if (config is null) return;
        for (uint board = 0; board < config.DiNum; board++)
            for (int port = 0; port < 16; port++) InputStates.Add(new BitStatus(board, port));
        for (uint board = 0; board < config.DoNum; board++)
            for (int port = 0; port < 16; port++) OutputStates.Add(new BitStatus(board, port));
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Positive(double value, string name)
        => Require(double.IsFinite(value) && value > 0, name + "必须是有限正数。");

    private static void ValidateConfig(ControlCardConfig config)
    {
        Require(config.AxisSingleConfigs is not null && config.AxisThreeConfigs is not null, "轴或坐标系配置为空。");
        var axes = Enum.GetValues<AxisType>();
        Require(config.AxisSingleConfigs.Length == axes.Length &&
            config.AxisSingleConfigs.All(x => x is not null && Enum.IsDefined(x.Axis)) &&
            config.AxisSingleConfigs.Select(x => x.Axis).Distinct().Count() == axes.Length, "轴配置必须完整且不能重复。");
        Require(config.AxisSingleConfigs.All(x => x.HardwareAxis >= 0 && x.HardwareAxis <= short.MaxValue) &&
            config.AxisSingleConfigs.Select(x => x.HardwareAxis).Distinct().Count() == axes.Length, "硬件轴号无效或重复。");
        Require(config.AxisThreeConfigs.All(x => x is not null && Enum.IsDefined(x.CrdType)) &&
            config.AxisThreeConfigs.Select(x => x.CrdType).Distinct().Count() == config.AxisThreeConfigs.Length, "坐标系配置无效或重复。");
        Require(config.CommandTimeoutMs is > 0 and <= 300000 && config.PollIntervalMs is > 0 and <= 1000,
            "等待超时须为1至300000毫秒，轮询间隔须为1至1000毫秒。");
        // 这里只限制内存资源上界；实际板卡可用节点数由相应驱动另外验证。
        Require(config.DiNum <= 4096 && config.DoNum <= 4096, "IO 节点数量超过软件资源上限。");
        foreach (var axis in config.AxisSingleConfigs)
        {
            Positive(axis.Equiv, "脉冲当量");
            Positive(axis.PtpVel, "定位速度"); Positive(axis.PtpAcc, "定位加速度"); Positive(axis.PtpDec, "定位减速度");
            Positive(axis.JogVel, "点动速度"); Positive(axis.JogAcc, "点动加速度"); Positive(axis.JogDec, "点动减速度");
            Require(axis.HomehighVel > 0 && axis.HomelowVel > 0 && axis.HomeAcc > 0 && axis.HomeOvertime > 0,
                "回零速度、加速度及超时必须大于零。");
            Require(double.IsFinite(axis.PtpPos), "默认位置必须是有限数值。");
            Require(double.IsFinite(axis.SoftLimitNegative) && double.IsFinite(axis.SoftLimitPositive) &&
                axis.SoftLimitNegative < axis.SoftLimitPositive, "软件限位上下界无效。");
        }
    }

    /// <summary>串行执行且捕获驱动异常；错误不能变成成功，也不将异常传播到 UI 事件。</summary>
    private bool Execute(string operation, Func<bool> action, out string message)
    {
        lock (_sync)
        {
            try
            {
                Require(_initialized, "控制卡未初始化或会话已关闭。");
                bool accepted = action();
                message = accepted ? operation + "请求已接受。" : operation + "请求未被接受或功能不支持。";
                return accepted;
            }
            catch (Exception exception)
            {
                message = operation + "失败：" + exception.Message;
                LogSafely(exception);
                return false;
            }
        }
    }

    private AxisStatus State(AxisType axis)
    {
        Require(Enum.IsDefined(axis), "轴类型无效。");
        return AxisStates.Single(x => x.Axis == axis);
    }

    private AxisSingleConfig AxisConfig(AxisType axis) => Config.AxisSingleConfigs.Single(x => x.Axis == axis);

    private AxisStatus RefreshAxis(AxisType axis)
    {
        var state = State(axis);
        try
        {
            Require(DoGetAxisStatus(axis), "轴状态读取失败。");
            Require(state.Valid, "轴状态无效，不能依据旧数据操作。");
            ObservePending(state);
            return state;
        }
        catch { state.Valid = false; _ready = false; throw; }
    }

    private AxisStatus MotionAxis(AxisType axis, bool allowPaused = false)
    {
        var state = RefreshAxis(axis);
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
    private void ObservePending(AxisStatus state)
    {
        if (!_pending.TryGetValue(state.Axis, out var pending)) return;
        if (state.Busy) { pending.ObservedBusy = true; return; }
        if (state.Paused) return;
        bool fresh = state.UpdatedAt >= pending.Started;
        bool atTarget = pending.Target.HasValue && state.Arrived && double.IsFinite(state.EncfPos) &&
            Math.Abs(state.EncfPos - pending.Target.Value) <= 1e-6;
        if (pending.ObservedBusy || fresh && (pending.Home ? state.Homed : atTarget)) _pending.Remove(state.Axis);
    }

    private PendingMotion MarkPending(AxisType axis, double? target = null, bool home = false, CrdType? coordinate = null)
    {
        var pending = new PendingMotion(target, home, CurrentTime, coordinate);
        _pending[axis] = pending;
        return pending;
    }

    /// <summary>
    /// 明确不支持表示驱动没有提交动作，只撤销本次建立的标记；其它异常不能证明未下发，保守保留。
    /// 引用身份比较避免清除另一操作或其它轴原有的待确认状态。
    /// </summary>
    private bool SubmitPending(Func<bool> action, params (AxisType Axis, PendingMotion Pending)[] created)
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

    private static void CheckTarget(AxisStatus state, AxisSingleConfig config, double target)
    {
        Require(double.IsFinite(target) && double.IsFinite(state.EncfPos), "位置必须是有限数值。");
        if (config.SoftLimitEnabled)
            Require(target >= config.SoftLimitNegative && target <= config.SoftLimitPositive, "目标位置超出软件限位。");
        // 触发限位后只允许离开该限位的方向，不能因任一限位触发就堵死退回动作。
        Require(!(state.PositiveLimit && target > state.EncfPos) && !(state.NegativeLimit && target < state.EncfPos),
            "目标方向上的硬件限位已触发。");
    }

    public bool GetAxisStatus(AxisType? axisType) => Execute("读取轴状态", () =>
    {
        if (axisType.HasValue) { RefreshAxis(axisType.Value); return true; }
        try
        {
            bool success = DoGetAxisStatus(null) && AxisStates.All(x => x.Valid);
            if (success) foreach (var state in AxisStates) ObservePending(state);
            if (!success) foreach (var state in AxisStates) state.Valid = false;
            _ready = success;
            return success;
        }
        catch { foreach (var state in AxisStates) state.Valid = false; _ready = false; throw; }
    }, out _);

    public bool SetAxisEnable(AxisType? axisType, bool value, out string message) => Execute(value ? "使能" : "关闭使能", () =>
    {
        var axes = axisType.HasValue ? new[] { axisType.Value } : AxisTypes.ToArray();
        foreach (var axis in axes)
        {
            State(axis);
            if (value)
            {
                var state = RefreshAxis(axis);
                Require(!state.Busy && !state.Alarm && !state.EmergencyStop, "运动、报警或急停时不能开启使能。");
            }
        }
        // 关闭使能是释放操作，不因反馈读取故障而阻止尝试关闭当前有效会话。
        bool result = DoSetAxisEnable(axisType, value);
        if (result) foreach (var axis in axes)
        {
            State(axis).Enable = value; State(axis).Paused = false;
            if (!value) { _pending.Remove(axis); _pausedMotion.Remove(axis); }
        }
        return result;
    }, out message);

    public bool ResetAxisFault(AxisType? axisType, out string message) => Execute("轴报警复位", () =>
    {
        foreach (var axis in axisType.HasValue ? new[] { axisType.Value } : AxisTypes.ToArray())
            Require(!RefreshAxis(axis).Busy, "轴运动时不能复位报警。");
        return DoResetAxisFault(axisType);
    }, out message);

    public bool GoHome(AxisType axisType, out string message) => Execute("回零启动", () =>
    {
        var state = MotionAxis(axisType);
        var pending = MarkPending(axisType, home: true);
        bool result = SubmitPending(() => DoGoHome(axisType, AxisConfig(axisType)), (axisType, pending));
        if (result) { state.Homed = false; state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool PtpSingleMoveTo(AxisType axisType, double pos, out string message) => Execute("绝对定位启动", () =>
    {
        var state = MotionAxis(axisType); var config = AxisConfig(axisType);
        CheckTarget(state, config, pos);
        var pending = MarkPending(axisType, pos);
        bool result = SubmitPending(() => DoPtpSingleMoveTo(axisType, pos, config), (axisType, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool PtpSingleMoveBy(AxisType axisType, double pos, out string message) => Execute("相对定位启动", () =>
    {
        var state = MotionAxis(axisType); var config = AxisConfig(axisType);
        Require(double.IsFinite(pos), "相对位移必须为有限数值。");
        var target = state.EncfPos + pos;
        CheckTarget(state, config, target);
        var pending = MarkPending(axisType, target);
        bool result = SubmitPending(() => DoPtpSingleMoveBy(axisType, pos, config), (axisType, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    public bool PtpSinglePause(AxisType axisType, out string message) => Execute("单轴暂停", () =>
    {
        var state = RefreshAxis(axisType);
        Require(state.Enable && state.Busy, "轴未使能或未运行，不能暂停。");
        bool result = DoPtpSinglePause(axisType);
        // SDK 没有暂停反馈位时，这仅记录本会话已接受暂停请求，不能解释为硬件确认。
        if (result)
        {
            state.Paused = true;
            if (_pending.Remove(axisType, out var motion)) _pausedMotion[axisType] = motion;
        }
        return result;
    }, out message);

    public bool PtpSingleResume(AxisType axisType, out string message) => Execute("单轴恢复", () =>
    {
        var state = MotionAxis(axisType, allowPaused: true);
        Require(state.Paused, "本会话没有已接受的暂停请求。");
        bool result = DoPtpSingleResume(axisType);
        if (result)
        {
            state.Paused = false;
            if (_pausedMotion.Remove(axisType, out var motion)) MarkPending(axisType, motion.Target, motion.Home, motion.Coordinate);
            else MarkPending(axisType);
        }
        return result;
    }, out message);

    public bool JogSingleMove(AxisType axisType, MoveDirection moveDirection, out string message) => Execute("点动启动", () =>
    {
        Require(Enum.IsDefined(moveDirection), "点动方向无效。");
        var state = MotionAxis(axisType); var config = AxisConfig(axisType);
        bool positive = moveDirection == MoveDirection.正向;
        Require(!(positive && state.PositiveLimit) && !(!positive && state.NegativeLimit), "点动方向限位已触发。");
        Require(double.IsFinite(state.EncfPos), "当前位置无效。");
        if (config.SoftLimitEnabled)
            Require(positive ? state.EncfPos < config.SoftLimitPositive : state.EncfPos > config.SoftLimitNegative,
                "点动方向软件限位已触发。");
        var pending = MarkPending(axisType);
        bool result = SubmitPending(() => DoJogSingleMove(axisType, moveDirection, config), (axisType, pending));
        if (result) { state.Arrived = false; state.Paused = false; }
        return result;
    }, out message);

    /// <summary>兼容旧接口；需要知道停止失败的调用者应使用 TryStop。</summary>
    public void Stop(AxisType? axisType) => TryStop(axisType, AxisStopMode.减速停止, out _);

    public bool TryStop(AxisType? axisType, AxisStopMode mode, out string message) => Execute("停止", () =>
    {
        Require(Enum.IsDefined(mode), "停止模式无效。");
        if (axisType.HasValue) State(axisType.Value);
        DoStop(axisType, mode);
        foreach (var state in AxisStates.Where(x => !axisType.HasValue || x.Axis == axisType))
        { state.Paused = false; _pending.Remove(state.Axis); _pausedMotion.Remove(state.Axis); }
        if (!axisType.HasValue) foreach (var state in CrdStates) state.Paused = false;
        return true;
    }, out message);

    /// <summary>
    /// 有界等待“当前 SDK 轴状态为空闲”，每轮读取后立即释放锁，Stop/Close 不会被等待循环占用。
    /// 空闲不等同于任意定位/回零成功；刚接受命令而 Busy 尚未置位时也可能空闲，调用方应另核对 Arrived/Homed。
    /// 原生单次读取的超时由厂商 SDK 控制；本方法无法中止卡住的非托管函数。
    /// </summary>
    public bool WaitForAxisIdle(AxisType axisType, TimeSpan timeout, CancellationToken cancellationToken, out string message)
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
                poll = Config.PollIntervalMs;
                var state = RefreshAxis(axisType);
                Require(!state.Alarm && !state.EmergencyStop, "等待期间轴报警或急停。");
                if (!state.Busy && !_pending.ContainsKey(axisType))
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

    private CrdStatus RefreshCoordinate(CrdType type)
    {
        Require(Enum.IsDefined(type), "坐标系编号无效。");
        var state = CrdStates.Single(x => x.CrdType == type);
        try
        {
            Require(DoGetCrdStatus(type) && state.Valid, "坐标系状态读取失败或无效。");
            return state;
        }
        catch { state.Valid = false; throw; }
    }

    public bool GetCrdStatus(CrdType? crdType) => Execute("读取坐标系状态", () =>
    {
        if (crdType.HasValue) { RefreshCoordinate(crdType.Value); return true; }
        bool result = DoGetCrdStatus(null) && CrdStates.All(x => x.Valid);
        if (!result) foreach (var state in CrdStates) state.Valid = false;
        return result;
    }, out _);

    public bool CrdLineMoveTo_XYZ(CrdType crdType, List<PosXYZ> pos, out string message) => Execute("XYZ直线轨迹启动", () =>
    {
        var state = RefreshCoordinate(crdType);
        Require(state.Exist && !state.Busy && !state.Paused && !state.Alarm, "坐标系未创建、运动中、暂停中或报警。");
        Require(pos is not null && pos.Count is > 0 and <= 10000 && pos.All(x => x is not null), "轨迹点列表为空或超出数量上限。");
        var config = Config.AxisThreeConfigs.SingleOrDefault(x => x.CrdType == crdType)
            ?? throw new InvalidOperationException("没有对应坐标系配置。");
        AxisType[] axes = [config.Axis_X, config.Axis_Y, config.Axis_Z];
        Require(axes.Distinct().Count() == 3 && axes.All(x => Enum.IsDefined(x)), "XYZ轴映射无效或重复。");
        Positive(config.CrdLineVel, "轨迹速度"); Positive(config.CrdLineAcc, "轨迹加速度");
        var states = axes.Select(x => MotionAxis(x)).ToArray();
        var points = pos.Select(x => new PosXYZ { Pos_X = x.Pos_X, Pos_Y = x.Pos_Y, Pos_Z = x.Pos_Z }).ToList();
        foreach (var point in points)
        {
            double[] targets = [point.Pos_X, point.Pos_Y, point.Pos_Z];
            for (int i = 0; i < axes.Length; i++) CheckTarget(states[i], AxisConfig(axes[i]), targets[i]);
        }
        var last = points[^1];
        double[] finalTargets = [last.Pos_X, last.Pos_Y, last.Pos_Z];
        var pending = axes.Select((axis, index) => (axis, MarkPending(axis, finalTargets[index], coordinate: crdType))).ToArray();
        bool result = SubmitPending(() => DoCrdLineMoveTo_XYZ(crdType, points, config), pending);
        if (result)
        {
            state.Paused = false;
            for (int i = 0; i < axes.Length; i++) states[i].Arrived = false;
        }
        return result;
    }, out message);

    public bool CrdPause(CrdType crdType, out string message) => Execute("坐标系暂停", () =>
    {
        var state = RefreshCoordinate(crdType);
        Require(state.Exist && state.Busy, "坐标系不存在或未运行。");
        bool result = DoCrdPause(crdType);
        if (result)
        {
            state.Paused = true;
            var config = Config.AxisThreeConfigs.Single(x => x.CrdType == crdType);
            foreach (var axis in new[] { config.Axis_X, config.Axis_Y, config.Axis_Z })
            {
                State(axis).Paused = true;
                if (_pending.Remove(axis, out var motion)) _pausedMotion[axis] = motion;
            }
        }
        return result;
    }, out message);

    public bool CrdResume(CrdType crdType, out string message) => Execute("坐标系恢复", () =>
    {
        var state = RefreshCoordinate(crdType);
        Require(state.Exist && state.Paused && !state.Busy && !state.Alarm, "坐标系未暂停、仍运动或报警。");
        var config = Config.AxisThreeConfigs.Single(x => x.CrdType == crdType);
        foreach (var axis in new[] { config.Axis_X, config.Axis_Y, config.Axis_Z }) MotionAxis(axis, allowPaused: true);
        bool result = DoCrdResume(crdType);
        if (result)
        {
            state.Paused = false;
            foreach (var axis in new[] { config.Axis_X, config.Axis_Y, config.Axis_Z })
            {
                State(axis).Paused = false;
                if (_pausedMotion.Remove(axis, out var motion)) MarkPending(axis, motion.Target, motion.Home, motion.Coordinate);
                else MarkPending(axis, coordinate: crdType);
            }
        }
        return result;
    }, out message);

    public bool CrdClr(CrdType crdType, out string message) => Execute("清空坐标系缓存", () =>
    {
        var state = RefreshCoordinate(crdType);
        Require(state.Exist && !state.Busy, "坐标系不存在或仍在运动。");
        bool result = DoCrdClr(crdType);
        if (result)
        {
            state.Paused = false;
            var config = Config.AxisThreeConfigs.Single(x => x.CrdType == crdType);
            foreach (var axis in new[] { config.Axis_X, config.Axis_Y, config.Axis_Z })
            {
                // 清空闲坐标系不能解除同轴上独立运动或另一个坐标系的待确认门控。
                bool ownPending = _pending.TryGetValue(axis, out var motion) && motion.Coordinate == crdType;
                bool ownPaused = _pausedMotion.TryGetValue(axis, out var paused) && paused.Coordinate == crdType;
                if (ownPending) _pending.Remove(axis);
                if (ownPaused) _pausedMotion.Remove(axis);
                if (ownPending || ownPaused) State(axis).Paused = false;
            }
        }
        return result;
    }, out message);

    public bool InputGetStatus(out string message) => Execute("读取输入", DoInputGetStatus, out message);
    public bool OutputGetStatus(out string message) => Execute("读取输出", DoOutputGetStatus, out message);

    public bool OutputSetStatus(BitStatus bitStatus, bool value, out string message) => Execute("设置输出", () =>
    {
        ArgumentNullException.ThrowIfNull(bitStatus);
        Require(bitStatus.Port is >= 0 and < 16 && bitStatus.Board < Config.DoNum &&
            OutputStates.Any(x => x.Board == bitStatus.Board && x.Port == bitStatus.Port), "输出地址不属于当前控制卡配置。");
        // 不把可变的调用方对象传入驱动，避免排队后参数被其它线程更改。
        return DoOutputSetStatus(new BitStatus(bitStatus.Board, bitStatus.Port), value);
    }, out message);

    protected abstract bool DoInit();
    protected abstract bool DoConfigure();
    protected abstract bool DoGoHome(AxisType axisType, AxisSingleConfig axisSingleConfig);
    protected abstract void DoStop(AxisType? axisType, AxisStopMode stopMode);
    protected abstract void DoClose();
    protected abstract bool DoGetAxisStatus(AxisType? axisType);
    protected abstract bool DoSetAxisEnable(AxisType? axisType, bool value);
    /// <summary>只有明确实现报警复位的厂商才能覆盖；清状态不自动等同驱动报警复位。</summary>
    protected virtual bool DoResetAxisFault(AxisType? axisType) => false;
    protected abstract bool DoPtpSingleMoveTo(AxisType axisType, double pos, AxisSingleConfig axisSingleConfig);
    protected abstract bool DoPtpSingleMoveBy(AxisType axisType, double pos, AxisSingleConfig axisSingleConfig);
    protected abstract bool DoPtpSinglePause(AxisType axisType);
    protected abstract bool DoPtpSingleResume(AxisType axisType);
    protected abstract bool DoJogSingleMove(AxisType axisType, MoveDirection moveDirection, AxisSingleConfig axisSingleConfig);
    protected abstract bool DoGetCrdStatus(CrdType? crdType);
    protected abstract bool DoCrdLineMoveTo_XYZ(CrdType crdType, List<PosXYZ> pos, AxisThreeConfig axisThreeConfig);
    protected abstract bool DoCrdPause(CrdType crdType);
    protected abstract bool DoCrdResume(CrdType crdType);
    protected abstract bool DoCrdClr(CrdType crdType);
    protected abstract bool DoInputGetStatus();
    protected abstract bool DoOutputGetStatus();
    protected abstract bool DoOutputSetStatus(BitStatus bitStatus, bool value);
}

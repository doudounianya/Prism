using Company.Logger;
using System.Diagnostics.CodeAnalysis;

namespace Company.Hardware.ControlCard;

/// <summary>
/// 控制卡公共入口。每次原生调用都在同一把锁内执行；状态刷新和指令准入不可被另一个指令插入。
/// 驱动只更新受保护的内部状态，公开集合返回克隆，调用方不能修改门控依据。
/// bool 仅表示请求被接受；运动、回零和插补是否完成必须读取实际状态，不能从返回值推断。
/// </summary>
public abstract partial class MotionControllerBase : IMotionController
{
    #region 会话、生命周期与统一执行

    private readonly object _sync = new();
    private bool _initialized;
    private bool _ready;
    private long _session;
    // 初始化状态与资源所有权分离：关闭失败时仍持有资源，不能以重新Init覆盖旧句柄。
    private bool _resourcesMayExist;
    private sealed class PendingMotion(double? target, bool home, CoordinateId? coordinate)
    {
        internal readonly double? Target = target;
        internal readonly bool Home = home;
        internal readonly CoordinateId? Coordinate = coordinate;
        internal bool ObservedBusy;
    }
    private readonly Dictionary<AxisId, PendingMotion> _pending = [];
    private readonly Dictionary<AxisId, PendingMotion> _pausedMotion = [];

    public bool IsInitialized { get { lock (_sync) return _initialized; } }
    public bool IsReady { get { lock (_sync) return _ready; } }
    protected MotionConfiguration Configuration { get; private set; } = null!;
    /// <summary>真实驱动覆盖为 true；仿真不会加载任何厂商库。</summary>
    protected virtual bool RequiresNativeHardware => false;
    /// <summary>实际驱动使用系统UTC；仿真可覆盖为受控时钟，反馈与待确认时间必须同源。</summary>
    protected virtual DateTimeOffset CurrentTime => DateTimeOffset.UtcNow;
    /// <summary>仅供仿真工装等受控状态注入，复用与SDK调用完全相同的串行边界。</summary>
    protected T Synchronize<T>(Func<T> action) { lock (_sync) return action(); }
    protected IEnumerable<AxisId> AxisIds => Configuration?.Axes.Select(a => a.Axis) ?? Enumerable.Empty<AxisId>();
    protected IEnumerable<CoordinateId> CoordinateIds => Configuration?.Coordinates.Select(c => c.CoordinateId) ?? Enumerable.Empty<CoordinateId>();
    protected virtual bool SupportsConfiguredSynchronization => false;

    protected List<AxisState> AxisStateBuffer { get; } = [];
    protected List<CoordinateState> CoordinateStateBuffer { get; } = [];
    protected List<DigitalPointState> InputStateBuffer { get; } = [];
    protected List<DigitalPointState> OutputStateBuffer { get; } = [];

    public IReadOnlyList<AxisState> AxisStates { get { lock (_sync) return AxisStateBuffer.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<CoordinateState> CoordinateStates { get { lock (_sync) return CoordinateStateBuffer.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<DigitalPointState> InputStates { get { lock (_sync) return InputStateBuffer.Select(x => x.Snapshot()).ToArray(); } }
    public IReadOnlyList<DigitalPointState> OutputStates { get { lock (_sync) return OutputStateBuffer.Select(x => x.Snapshot()).ToArray(); } }

    protected MotionControllerBase() => ResetStates(null);

    /// <summary>
    /// 显式启用后才打开卡。直接传入配置并在此建立深快照，不订阅运行中任意替换的可变配置；修改配置后应停止并重新初始化。
    /// 任一步失败都尝试关闭资源，不能遗留 Initialized=true 或把真卡失败替换成仿真成功。
    /// </summary>
    public bool Initialize(MotionConfiguration configuration)
    {
        lock (_sync)
        {
            if (_initialized) { Logs.LogWarning("控制卡已经初始化，请先关闭再修改配置。"); return false; }
            if (_resourcesMayExist && !TryReleaseResources())
            { Logs.LogWarning("上一次控制卡资源尚未释放，拒绝重新初始化。"); return false; }
            bool opening = false;
            try
            {
                ArgumentNullException.ThrowIfNull(configuration);
                Configuration = configuration.Snapshot();
                ValidateConfig(Configuration);
                Require(Configuration.Enabled, "控制卡未启用。");
                Require(!RequiresNativeHardware || Configuration.AllowNativeHardware, "真实控制卡未授权启用。");
                Require(!Configuration.Synchronizations.Any(s => s.Enabled) || SupportsConfiguredSynchronization,
                    "当前驱动尚未实现配置中的电子齿轮同步，请先完成对应驱动能力适配；未打开设备。");
                ResetStates(Configuration);
                opening = true;
                _resourcesMayExist = true;
                Require(OpenCore(), "控制卡初始化失败。");
                Require(ConfigureCore(), "控制卡参数配置失败。");
                // 首次真实读取成功才发布就绪；轴反馈未知的卡不能接受运动命令。
                Require(ReadAxisStatesCore(null) && AxisStateBuffer.All(s => s.Valid), "控制卡首次轴状态读取失败。");
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
                    try { StopCore(null, StopMode.Decelerate); }
                    catch (Exception exception) { LogSafely(exception); }
                }
                if (_resourcesMayExist) TryReleaseResources();
            }
            finally { InvalidateSession(); }
        }
    }

    private bool TryReleaseResources()
    {
        try { CloseCore(); _resourcesMayExist = false; return true; }
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
        _pending.Clear(); _pausedMotion.Clear(); _coordinateClaims.Clear(); _coordinateVersions.Clear();
        foreach (var state in AxisStateBuffer) { state.Valid = false; state.Enable = false; state.Busy = false; state.Paused = false; state.Homing = false; }
        foreach (var state in CoordinateStateBuffer) { state.Valid = false; state.Busy = false; state.Paused = false; state.Enabled = false; state.Queued = false; state.Cleared = false; }
        foreach (var state in InputStateBuffer) state.Valid = false;
        foreach (var state in OutputStateBuffer) state.Valid = false;
        Configuration = null!;
    }

    private void ResetStates(MotionConfiguration? config)
    {
        AxisStateBuffer.Clear(); CoordinateStateBuffer.Clear(); InputStateBuffer.Clear(); OutputStateBuffer.Clear();
        _pending.Clear(); _pausedMotion.Clear(); _coordinateClaims.Clear(); _coordinateVersions.Clear();
        foreach (var axis in config?.Axes.Select(a => a.Axis) ?? Enumerable.Empty<AxisId>()) AxisStateBuffer.Add(new AxisState { Axis = axis });
        foreach (var coordinate in config?.Coordinates.Select(c => c.CoordinateId) ?? Enumerable.Empty<CoordinateId>()) CoordinateStateBuffer.Add(new CoordinateState { CoordinateId = coordinate });
        if (config is null) return;
        for (uint board = 0; board < config.InputBankCount; board++)
            for (int port = 0; port < 16; port++) InputStateBuffer.Add(new DigitalPointState(board, port));
        for (uint board = 0; board < config.OutputBankCount; board++)
            for (int port = 0; port < 16; port++) OutputStateBuffer.Add(new DigitalPointState(board, port));
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Positive(double value, string name)
        => Require(double.IsFinite(value) && value > 0, name + "必须是有限正数。");

    private static void ValidateConfig(MotionConfiguration config) => config.Validate();

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

    // 以下声明只负责连接、配置和释放；各功能的驱动扩展点放在对应分文件中。
    protected abstract bool OpenCore();
    protected abstract bool ConfigureCore();
    protected abstract void CloseCore();

    #endregion

    #region 数字输入输出

    public bool RefreshInputs(out string message) => Execute("读取输入", ReadInputsCore, out message);
    public bool RefreshOutputs(out string message) => Execute("读取输出", ReadOutputsCore, out message);

    public bool WriteOutput(DigitalPointState point, bool value, out string message) => Execute("设置输出", () =>
    {
        ArgumentNullException.ThrowIfNull(point);
        Require(point.Bit is >= 0 and < 16 && point.Bank < Configuration.OutputBankCount &&
            OutputStateBuffer.Any(x => x.Bank == point.Bank && x.Bit == point.Bit), "输出地址不属于当前控制卡配置。");
        // 不把可变的调用方对象传入驱动，避免排队后参数被其它线程更改。
        return WriteOutputCore(new DigitalPointState(point.Bank, point.Bit), value);
    }, out message);

    // 厂商只覆盖本功能所需的扩展点；公共校验仍通过上方入口统一执行。
    protected abstract bool ReadInputsCore();
    protected abstract bool ReadOutputsCore();
    protected abstract bool WriteOutputCore(DigitalPointState bitStatus, bool value);

    #endregion

    #region 能力和身份快照

    /// <summary>
    /// 查询只读取驱动已经保存的型号、资源和状态；不得为展示一个按钮而初始化或读取SDK。
    /// 每次返回新的只读集合，调用者不能篡改内部准入依据。
    /// </summary>
    public IReadOnlyList<MotionCapability> GetCapabilities() => Synchronize(() =>
    {
        var described = DescribeCapabilities().ToDictionary(x => x.Feature);
        var result = Enum.GetValues<MotionFeature>().Select(feature =>
        {
            var capability = described.GetValueOrDefault(feature) ?? new MotionCapability(
                feature, CapabilitySupport.Unknown, false, CapabilityValidation.None, false,
                "此驱动尚未提供该功能的型号与协议依据。");
            bool ready = capability.Ready && _initialized && _ready;
            string reason = capability.Reason;
            if (capability.Ready && !ready) reason = "当前控制卡会话未就绪。";
            return capability with { Ready = ready, Reason = reason };
        }).ToArray();
        return (IReadOnlyList<MotionCapability>)Array.AsReadOnly(result);
    });

    /// <summary>具体驱动逐项报告；默认未知且未适配，绝不以继承了方法就声称支持。</summary>
    protected virtual IReadOnlyList<MotionCapability> DescribeCapabilities() => [];

    public MotionControllerIdentity GetIdentity() => Synchronize(DescribeIdentity);

    protected virtual MotionControllerIdentity DescribeIdentity() => new(
        "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", null, 0, 0, 0);

    #endregion
}

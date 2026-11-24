using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;
using Company.Logger;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Imc30G.Verification")]

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>
/// 汇川板卡实现。调用由基类串行化；这里只提交已校验的指令并读取真实状态。
/// 构造和模块注册不加载原生库、不打开卡；默认所有原生访问均关闭。
/// </summary>
public sealed partial class INOVANCEControlCard : MotionControllerBase
{
    private ulong cardHandle;
    private readonly IInovanceApi api;
    private readonly TimeProvider clock;
    private ImcApi.TRsouresNum resources;
    private short hardwareType, cycleTime;
    private int maximumHardwareAxes = 32; // 协议轴号范围，不代表现场存在32个物理轴。
    private bool resourcesVerified;
    private readonly Dictionary<AxisId, short> mappedAxes = [];
    private readonly Dictionary<AxisId, AxisBinding> axisBindings = [];
    private string systemVersion = "Unknown", fpgaVersion = "Unknown", apiVersion = "Unknown";
    private readonly record struct AxisBinding(short AxisNumber, short AxisId, short OutputChannel, short LocalEncoderSource);
    private InovanceNativeOptions options = new();
    /// <summary>初始化前设置；初始化会拍下独立快照，运行中修改此对象不会改变正在使用的协议。</summary>
    public InovanceNativeOptions Options { get; } = new();
    public INOVANCEControlCard() : this(new InovanceNativeApi(), TimeProvider.System)
        => InovanceNativeLibrary.RegisterResolver();
    /// <summary>仅内部离线工装可注入假SDK；公开界面/工厂始终使用真实转发且保持默认禁用。</summary>
    internal INOVANCEControlCard(IInovanceApi api, TimeProvider? timeProvider = null)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        clock = timeProvider ?? TimeProvider.System;
    }
    protected override DateTimeOffset CurrentTime => clock.GetUtcNow();
    protected override bool RequiresNativeHardware => true;

    protected override bool OpenCore()
    {
        if (!Configuration.AllowNativeHardware) throw new InvalidOperationException("原生硬件访问未开启。");
        foreach (var axis in Configuration.Axes) _ = HardwareAxis(axis); // 不合法轴号在开卡前拒绝。
        if (Configuration.Coordinates.Any(c => c.HardwareCoordinate is < 0 or > 3))
            throw new ArgumentOutOfRangeException(nameof(Configuration.Coordinates), "IMC控制器坐标系槽号必须为0至3。");
        InovanceNativeLibrary.EnsureSupportedProcess();
        if (cardHandle != 0) throw new InvalidOperationException("控制卡仍持有旧句柄。");
        options = Options.Snapshot();
        resourcesVerified = false; mappedAxes.Clear(); axisBindings.Clear(); homeFaults.Clear();
        ReadLocalApiIdentity();
        ulong opened = 0;
        try
        {
            int count = 0;
            Success(api.IMC_GetCardsNum(ref count), "读取控制卡数量");
            if (options.CardNumber < 0 || options.CardNumber >= count) throw new InvalidOperationException("配置的控制卡编号不存在。");
            Success(api.IMC_OpenCardHandle(options.CardNumber, ref opened), "打开控制卡");
            if (opened == 0) throw new InvalidOperationException("SDK 返回了零句柄。");
            Success(api.IMC_GetHwSysPara(opened, ref cycleTime, ref hardwareType), "核对通讯资源类型");
            if (hardwareType is < 0 or > 2) throw new InvalidOperationException("控制卡硬件资源类型未确认。");
            if (hardwareType != 2)
            {
                // 仅端子板类型没有EtherCAT轴，不能要求不存在的总线进入OP。
                uint master = 0;
                Success(api.IMC_GetECATMasterSts(opened, ref master), "读取 EtherCAT 主站状态");
                if (master != ImcApi.EC_MASTER_OP)
                {
                    if (!options.AllowConfigurationDownload) throw new InvalidOperationException("EtherCAT 未进入 OP，自动下载配置默认关闭。");
                    Success(api.IMC_DownLoadDeviceConfig(opened, ExistingFile(Configuration.DeviceParameterFile)), "下载设备配置");
                    Success(api.IMC_ScanCardECAT(opened, 1), "启动 EtherCAT 扫描");
                    Success(api.IMC_GetECATMasterSts(opened, ref master), "确认主站状态");
                    if (master != ImcApi.EC_MASTER_OP) throw new InvalidOperationException("扫描后未确认 OP，初始化被拒绝。");
                }
            }
            // 初始化不是覆盖控制器配置的授权；必须单独开启下载选项。
            if (options.AllowConfigurationDownload && !string.IsNullOrWhiteSpace(Configuration.SystemParameterFile))
                Success(api.IMC_DownLoadSystemConfig(opened, ExistingFile(Configuration.SystemParameterFile)), "下载系统配置");
            // 先核对固定长度的系统资源和逐轴绑定；虚轴的默认使能/到位不能作为实机就绪。
            ReadResourcesAndBindings(opened);
            cardHandle = opened; opened = 0;
            return true;
        }
        finally
        {
            // 无论 SDK 返回失败还是托管代码抛异常，本次打开的句柄都必须尝试释放。
            if (opened != 0)
            {
                try { Success(api.IMC_CloseCardHandle(opened), "释放初始化失败的句柄"); }
                catch (Exception ex)
                {
                    // 未确认释放时保留所有权，基类随后还可清理；不得遗忘句柄后再打开新卡。
                    cardHandle = opened;
                    Logs.LogError("初始化失败后的句柄释放未确认：" + ex.Message);
                }
            }
        }
    }

    protected override bool ConfigureCore()
    {
        ulong handle = Handle();
        // 不猜接线极性；未明确给出时保留控制器现有急停电平配置。
        if (options.EmergencyInputInverted is bool inverted)
            Success(api.IMC_SetEmgTrigLevelInv(handle, inverted ? (short)1 : (short)0), "设置已确认的急停电平");
        foreach (var axis in AxisIds)
        {
            var config = AxisConfig(axis); Positive(config.PulsesPerUnit, "当量");
            short number = HardwareAxis(config);
            if (options.ApplyAxisEquivalents) Success(api.IMC_SetAxEquiv(handle, number, [config.PulsesPerUnit]), "设置轴当量");
            else
            {
                double[] actual = new double[1];
                Success(api.IMC_GetAxEquiv(handle, number, actual), "核对现有轴当量");
                if (!double.IsFinite(actual[0]) || Math.Abs(actual[0] - config.PulsesPerUnit) > Math.Max(1, Math.Abs(config.PulsesPerUnit)) * 1e-9)
                    throw new InvalidOperationException($"轴 {axis} 的控制器当量与配置不一致，未自动覆盖设备参数。");
            }
        }
        return true;
    }

    protected override void CloseCore()
    {
        // 成功关闭后才清所有权；失败保留句柄并阻止重新打开，不谎报资源已经释放。
        ulong handle = cardHandle;
        if (handle == 0) return;
        List<string> failures = [];
        try { CloseCoordinates(); } catch (Exception ex) { failures.Add("轨迹清理：" + ex.Message); }
        try { CloseHomes(); } catch (Exception ex) { failures.Add("回零清理：" + ex.Message); }
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("；", failures));
        Success(api.IMC_CloseCardHandle(handle), "关闭控制卡句柄");
        cardHandle = 0;
        resourcesVerified = false; mappedAxes.Clear(); axisBindings.Clear();
    }
    protected override bool SetAxisEnabledCore(AxisId? axisId, bool enabled)
    {
        ulong handle = Handle();
        List<string> failures = [];
        if (!enabled)
        {
            // 下使能不能遗留持续插补使能或回零模式；停止未确认则保持资源占用供后续收尾。
            try { StopCoordinates(axisId, StopMode.Decelerate); } catch (Exception ex) { failures.Add(ex.Message); }
            try { RequestHomeStops(axisId, StopMode.Decelerate); } catch (Exception ex) { failures.Add(ex.Message); }
        }
        foreach (var axis in SelectAxes(axisId))
        {
            short number = HardwareAxis(AxisConfig(axis));
            try { Success(enabled ? api.IMC_AxServoOn(handle, number) : api.IMC_AxServoOff(handle, number), enabled ? "请求使能" : "请求关闭使能"); }
            catch (Exception ex)
            {
                if (enabled) throw;
                failures.Add($"{axis}：" + ex.Message);
            }
        }
        if (failures.Count != 0) throw new InvalidOperationException("关闭使能或关联任务清理未全部确认：" + string.Join("；", failures));
        return true;
    }
    protected override void StopCore(AxisId? axisId, StopMode mode)
    {
        ulong handle = Handle();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        List<string> failures = [];
        try { StopCoordinates(axisId, mode); } catch (Exception ex) { failures.Add("轨迹：" + ex.Message); }
        try { RequestHomeStops(axisId, mode); } catch (Exception ex) { failures.Add("回零：" + ex.Message); }
        // 即使一轴停止失败，也尝试停止其余轴，然后把失败交给基类统一报告。
        foreach (var axis in SelectAxes(axisId))
        {
            try { Success(api.IMC_AxMoveStop(handle, HardwareAxis(AxisConfig(axis)), (short)mode), "请求停止"); }
            catch (Exception ex) { failures.Add($"{axis}：{ex.Message}"); }
        }
        if (failures.Count != 0) throw new InvalidOperationException("部分轴停止未确认：" + string.Join("；", failures));
        foreach (var axis in SelectAxes(axisId).Where(axis => !homeSessions.ContainsKey(axis)))
        {
            // 明确停止是对已经退出回零的本地诊断的确认，不会清除物理伺服报警。
            homeFaults.Remove(axis);
            var state = AxisStateBuffer.Single(s => s.Axis == axis);
            state.FaultCode = 0; state.ErrorMessage = string.Empty;
        }
    }

    protected override bool ReadAxisStatesCore(AxisId? axisId)
    {
        var selected = SelectAxes(axisId);
        try
        {
            ulong handle = Handle();
            ObserveCoordinateAxes();
            // 每个逻辑轴按明确 HardwareAxis 读取，兼容不连续物理编号，也避免 null 转换。
            List<(AxisId Axis, int Raw, double P, double E, double V, double EV, double A, double EA)> samples = [];
            foreach (var axis in selected)
            {
                ValidateCurrentBinding(handle, axis);
                ObserveHome(axis);
                short number = HardwareAxis(AxisConfig(axis));
                int[] raw = new int[1];
                double[] p = new double[1], e = new double[1], v = new double[1], ev = new double[1], a = new double[1], ea = new double[1];
                Success(api.IMC_GetAxSts(handle, number, raw), "读取轴状态");
                Success(api.IMC_GetAxPrfPos(handle, number, p), "读取规划位置");
                Success(api.IMC_GetAxEncPos(handle, number, e), "读取反馈位置");
                Success(api.IMC_GetAxPrfVel(handle, number, v), "读取规划速度");
                Success(api.IMC_GetAxEncVel(handle, number, ev), "读取反馈速度");
                Success(api.IMC_GetAxPrfAcc(handle, number, a), "读取规划加速度");
                Success(api.IMC_GetAxEncAcc(handle, number, ea), "读取反馈加速度");
                foreach (double value in new[] { p[0], e[0], v[0], ev[0], a[0], ea[0] }) Finite(value, "轴反馈");
                samples.Add((axis, raw[0], p[0], e[0], v[0], ev[0], a[0], ea[0]));
            }
            // 全部读取成功才提交本批状态；任一失败则使本批状态失效，禁止用旧值准入新动作。
            var now = CurrentTime;
            foreach (var sample in samples)
            {
                var state = AxisStateBuffer.Single(s => s.Axis == sample.Axis);
                uint raw = unchecked((uint)sample.Raw);
                state.RawStatus = sample.Raw;
                state.Enable = (raw & ImcApi.AX_SVON_BIT) != 0; state.Busy = (raw & ImcApi.AX_BUSY_BIT) != 0;
                state.Arrived = (raw & ImcApi.AX_ARRIVE_BIT) != 0;
                state.Alarm = (raw & (ImcApi.AX_ALARM_BIT | ImcApi.AX_ERRPOS_BIT | ImcApi.AX_SW_ABNOR_BIT)) != 0;
                state.Warning = (raw & ImcApi.AX_WARNING_BIT) != 0;
                state.Mapped = resourcesVerified && mappedAxes.ContainsKey(sample.Axis);
                state.PositiveLimit = (raw & (ImcApi.AX_POSLMT_BIT | ImcApi.AX_SOFT_POSLMT_BIT)) != 0;
                state.NegativeLimit = (raw & (ImcApi.AX_NEGLMT_BIT | ImcApi.AX_SOFT_NEGLMT_BIT)) != 0;
                state.EmergencyStop = (raw & ImcApi.AX_EMG_STOP_BIT) != 0;
                // C02定义bit13为HOME输入；未知固件不把false解释成开关确实未触发。
                state.OriginKnown = options.OriginInputBitConfirmed;
                state.Origin = state.OriginKnown && (raw & ImcApi.AX_HM_BIT) != 0;
                if (!state.Enable || state.Alarm || state.EmergencyStop) state.Homed = false;
                state.Homing = homeSessions.ContainsKey(sample.Axis);
                // Paused 保留基类记录的本会话已接受请求，它不是硬件确认位。
                state.CommandPosition = sample.P; state.ActualPosition = sample.E; state.CommandVelocity = sample.V;
                state.ActualVelocity = sample.EV; state.CommandAcceleration = sample.A; state.ActualAcceleration = sample.EA;
                state.UpdatedAt = now; state.Valid = true;
            }
            return true;
        }
        catch { foreach (var axis in selected) AxisStateBuffer.Single(s => s.Axis == axis).Valid = false; throw; }
    }

    protected override bool MoveAbsoluteCore(AxisId axis, double pos, AxisConfiguration config) => Ptp(pos, config, 0);
    protected override bool MoveRelativeCore(AxisId axis, double pos, AxisConfiguration config) => Ptp(pos, config, 1);
    private bool Ptp(double pos, AxisConfiguration config, short relative)
    {
        Finite(pos, "运动目标");
        MotionParameters(config, config.PositioningVelocity, config.PositioningAcceleration, config.PositioningDeceleration);
        // 必须使用本次传入目标，不使用配置中的默认 PtpPos。
        Success(api.IMC_StartPtpMove(Handle(), HardwareAxis(config), pos, relative), relative == 0 ? "请求绝对运动" : "请求相对运动");
        return true;
    }
    protected override bool PauseAxisCore(AxisId axis)
    {
        Success(api.IMC_PauseMove(Handle(), HardwareAxis(AxisConfig(axis))), "请求暂停"); return true;
    }
    protected override bool ResumeAxisCore(AxisId axis)
    {
        Success(api.IMC_ResumeMove(Handle(), HardwareAxis(AxisConfig(axis))), "请求恢复"); return true;
    }
    protected override bool JogAxisCore(AxisId axis, JogDirection direction, AxisConfiguration config)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        MotionParameters(config, config.JogVelocity, config.JogAcceleration, config.JogDeceleration);
        Success(api.IMC_StartJogMove(Handle(), HardwareAxis(config), direction == JogDirection.Positive ? config.JogVelocity : -config.JogVelocity), "请求点动"); return true;
    }
    private void MotionParameters(AxisConfiguration config, double velocity, double acceleration, double deceleration)
    {
        Positive(velocity, "速度"); Positive(acceleration, "加速度"); Positive(deceleration, "减速度");
        Success(api.IMC_SetSingleAxMvPara(Handle(), HardwareAxis(config), velocity, acceleration, deceleration), "设置本次运动参数");
    }
    protected override bool ResetAxisFaultCore(AxisId? axis)
        => throw new NotSupportedException("IMC_ClrAxSts 仅说明清除轴状态，未确认驱动报警复位协议，未下发猜测的复位命令。");

    protected override bool ReadInputsCore() => ReadIo(true);
    protected override bool ReadOutputsCore() => ReadIo(false);
    private bool ReadIo(bool input)
    {
        var states = input ? InputStateBuffer : OutputStateBuffer;
        try
        {
            uint groups = input ? Configuration.InputBankCount : Configuration.OutputBankCount;
            if (groups > 2048) throw new ArgumentOutOfRangeException(nameof(groups), "IO 超过有符号16位地址范围。");
            int count = checked((int)groups);
            if (states.Count != checked(count * 16)) throw new InvalidOperationException("IO 状态点数与配置不一致。");
            if (count == 0) return true;
            short[] words = new short[count];
            Success(input ? api.IMC_GetEcatPackDi(Handle(), 0, words, checked((short)count)) : api.IMC_GetEcatPackDo(Handle(), 0, words, checked((short)count)), input ? "读取 DI 组" : "读取 DO 组");
            var now = CurrentTime;
            for (int group = 0; group < count; group++)
                for (int bit = 0; bit < 16; bit++)
                {
                    var state = states[group * 16 + bit];
                    if (state.Bank != (uint)group || state.Bit != bit) throw new InvalidOperationException("IO 映射顺序不一致。");
                    state.Value = DecodeIoBit(words[group], bit);
                    state.UpdatedAt = now; state.Valid = true;
                }
            return true;
        }
        catch { foreach (var state in states) state.Valid = false; throw; }
    }
    protected override bool WriteOutputCore(DigitalPointState point, bool value)
    {
        ArgumentNullException.ThrowIfNull(point);
        short index = OutputIndex(Configuration.OutputBankCount, point.Bank, point.Bit);
        Success(api.IMC_SetEcatDoBit(Handle(), index, value ? (short)1 : (short)0), "请求 DO 输出"); return true;
    }

    /// <summary>纯计算的组位解码，符号位15按无符号word处理，便于离线覆盖最高位。</summary>
    internal static bool DecodeIoBit(short word, int bit)
    {
        if (bit is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(bit));
        return (unchecked((ushort)word) & (1 << bit)) != 0;
    }
    /// <summary>先校验组和位，再计算SDK输出编号，禁止无效端口折算成其他DO。</summary>
    internal static short OutputIndex(uint groups, uint board, int port)
    {
        if (board >= groups || port is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(port), "DO 组或端口越界。");
        uint index = checked(board * 16u + (uint)port);
        if (index > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(board), "DO 编号超过 SDK 范围。");
        return checked((short)index);
    }

    private ulong Handle() => cardHandle != 0 ? cardHandle : throw new InvalidOperationException("控制卡未打开或句柄已释放。");
    private AxisConfiguration AxisConfig(AxisId axis) => Configuration.Axes.Single(c => c.Axis == axis);
    private AxisId[] SelectAxes(AxisId? axis)
    {
        if (axis is not null && (!axis.Value.IsValid || !AxisIds.Contains(axis.Value))) throw new ArgumentOutOfRangeException(nameof(axis));
        return axis is null ? AxisIds.ToArray() : [axis.Value];
    }
    private static short HardwareAxis(AxisConfiguration config)
    {
        if (config.HardwareAxis < 0 || config.HardwareAxis > 31) throw new ArgumentOutOfRangeException(nameof(config.HardwareAxis));
        return checked((short)config.HardwareAxis);
    }
    private static void Finite(double value, string name)
    { if (!double.IsFinite(value)) throw new ArgumentException(name + "必须是有限数字。"); }
    private static void Positive(double value, string name)
    { Finite(value, name); if (value <= 0) throw new ArgumentOutOfRangeException(name, name + "必须大于零。"); }
    private static void Success(uint result, string operation)
    { if (result != ImcApi.EXE_SUCCESS) throw new InvalidOperationException($"{operation}失败，SDK 返回码 {result}。"); }
    private static string ExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("控制器配置路径未设置。");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("控制器配置文件不存在。", full);
        return full;
    }
}

using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;
using Company.Logger;
using System.IO;

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>
/// 汇川板卡实现。调用由基类串行化；这里只提交已校验的指令并读取真实状态。
/// 构造和模块注册不加载原生库、不打开卡；默认所有原生访问均关闭。
/// </summary>
public sealed class INOVANCEControlCard : ControlCardBase
{
    private ulong cardHandle;
    private InovanceNativeOptions options = new();
    /// <summary>初始化前设置；初始化会拍下独立快照，运行中修改此对象不会改变正在使用的协议。</summary>
    public InovanceNativeOptions Options { get; } = new();
    public INOVANCEControlCard() => InovanceNativeLibrary.RegisterResolver();
    protected override bool RequiresNativeHardware => true;

    protected override bool DoInit()
    {
        if (!Config.AllowNativeHardware) throw new InvalidOperationException("原生硬件访问未开启。");
        InovanceNativeLibrary.EnsureSupportedProcess();
        if (cardHandle != 0) throw new InvalidOperationException("控制卡仍持有旧句柄。");
        options = Options.Snapshot();
        ulong opened = 0;
        try
        {
            int count = 0;
            Success(ImcApi.IMC_GetCardsNum(ref count), "读取控制卡数量");
            if (options.CardNumber < 0 || options.CardNumber >= count) throw new InvalidOperationException("配置的控制卡编号不存在。");
            Success(ImcApi.IMC_OpenCardHandle(options.CardNumber, ref opened), "打开控制卡");
            if (opened == 0) throw new InvalidOperationException("SDK 返回了零句柄。");
            uint master = 0;
            Success(ImcApi.IMC_GetECATMasterSts(opened, ref master), "读取 EtherCAT 主站状态");
            if (master != ImcApi.EC_MASTER_OP)
            {
                if (!options.AllowConfigurationDownload) throw new InvalidOperationException("EtherCAT 未进入 OP，自动下载配置默认关闭。");
                Success(ImcApi.IMC_DownLoadDeviceConfig(opened, ExistingFile(Config.DeviceFile)), "下载设备配置");
                Success(ImcApi.IMC_ScanCardECAT(opened, 1), "启动 EtherCAT 扫描");
                Success(ImcApi.IMC_GetECATMasterSts(opened, ref master), "确认主站状态");
                if (master != ImcApi.EC_MASTER_OP) throw new InvalidOperationException("扫描后未确认 OP，初始化被拒绝。");
            }
            // 初始化不是覆盖控制器配置的授权；必须单独开启下载选项。
            if (options.AllowConfigurationDownload && !string.IsNullOrWhiteSpace(Config.SystemFile))
                Success(ImcApi.IMC_DownLoadSystemConfig(opened, ExistingFile(Config.SystemFile)), "下载系统配置");
            cardHandle = opened; opened = 0;
            return true;
        }
        finally
        {
            // 无论 SDK 返回失败还是托管代码抛异常，本次打开的句柄都必须尝试释放。
            if (opened != 0)
            {
                try { Success(ImcApi.IMC_CloseCardHandle(opened), "释放初始化失败的句柄"); }
                catch (Exception ex)
                {
                    // 未确认释放时保留所有权，基类随后还可清理；不得遗忘句柄后再打开新卡。
                    cardHandle = opened;
                    Logs.LogError("初始化失败后的句柄释放未确认：" + ex.Message);
                }
            }
        }
    }

    protected override bool DoConfigure()
    {
        ulong handle = Handle();
        // 不猜接线极性；未明确给出时保留控制器现有急停电平配置。
        if (options.EmergencyInputInverted is bool inverted)
            Success(ImcApi.IMC_SetEmgTrigLevelInv(handle, inverted ? (short)1 : (short)0), "设置已确认的急停电平");
        foreach (var axis in AxisTypes)
        {
            var config = AxisConfig(axis); Positive(config.Equiv, "当量");
            short number = HardwareAxis(config);
            if (options.ApplyAxisEquivalents) Success(ImcApi.IMC_SetAxEquiv(handle, number, [config.Equiv]), "设置轴当量");
            else
            {
                double[] actual = new double[1];
                Success(ImcApi.IMC_GetAxEquiv(handle, number, actual), "核对现有轴当量");
                if (!double.IsFinite(actual[0]) || Math.Abs(actual[0] - config.Equiv) > Math.Max(1, Math.Abs(config.Equiv)) * 1e-9)
                    throw new InvalidOperationException($"轴 {axis} 的控制器当量与配置不一致，未自动覆盖设备参数。");
            }
        }
        return true;
    }

    protected override void DoClose()
    {
        // 成功关闭后才清所有权；失败保留句柄并阻止重新打开，不谎报资源已经释放。
        ulong handle = cardHandle;
        if (handle == 0) return;
        Success(ImcApi.IMC_CloseCardHandle(handle), "关闭控制卡句柄");
        cardHandle = 0;
    }
    protected override bool DoSetAxisEnable(AxisType? axisType, bool enabled)
    {
        ulong handle = Handle();
        foreach (var axis in SelectAxes(axisType))
        {
            short number = HardwareAxis(AxisConfig(axis));
            Success(enabled ? ImcApi.IMC_AxServoOn(handle, number) : ImcApi.IMC_AxServoOff(handle, number), enabled ? "请求使能" : "请求关闭使能");
        }
        return true;
    }
    protected override void DoStop(AxisType? axisType, AxisStopMode mode)
    {
        ulong handle = Handle();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        List<string> failures = [];
        // 即使一轴停止失败，也尝试停止其余轴，然后把失败交给基类统一报告。
        foreach (var axis in SelectAxes(axisType))
        {
            try { Success(ImcApi.IMC_AxMoveStop(handle, HardwareAxis(AxisConfig(axis)), (short)mode), "请求停止"); }
            catch (Exception ex) { failures.Add($"{axis}：{ex.Message}"); }
        }
        if (failures.Count != 0) throw new InvalidOperationException("部分轴停止未确认：" + string.Join("；", failures));
    }

    protected override bool DoGoHome(AxisType axisType, AxisSingleConfig config)
    {
        if (!options.HomingAxisUnitsPerSecondConfirmed)
            throw new NotSupportedException("回零单位未确认；明确采用轴单位/s、轴单位/s²后才能启用 InUnit 回零。");
        Positive(config.HomehighVel, "回零高速"); Positive(config.HomelowVel, "回零低速"); Positive(config.HomeAcc, "回零加速度");
        if (config.HomelowVel > config.HomehighVel || config.HomeOvertime == 0) throw new ArgumentException("回零低速/高速或超时配置不合法。");
        // InUnit 声明接受 double 的 unit/s、unit/s²；不再进行 UInt32 乘法或截断小数当量。
        ImcApi.THomingParaInUint parameters = new()
        {
            homeMethod = config.HomeMethod, offset = 0, highVel = config.HomehighVel,
            lowVel = config.HomelowVel, acc = config.HomeAcc, overtime = config.HomeOvertime, posSrc = 0
        };
        Success(ImcApi.IMC_StartHomingInUnit(Handle(), HardwareAxis(config), ref parameters), "请求回零");
        return true; // 只表示指令被接受，不伪造 Homed=True。
    }

    protected override bool DoGetAxisStatus(AxisType? axisType)
    {
        var selected = SelectAxes(axisType);
        try
        {
            ulong handle = Handle();
            // 每个逻辑轴按明确 HardwareAxis 读取，兼容不连续物理编号，也避免 null 转换。
            List<(AxisType Axis, int Raw, double P, double E, double V, double EV, double A, double EA)> samples = [];
            foreach (var axis in selected)
            {
                short number = HardwareAxis(AxisConfig(axis));
                int[] raw = new int[1];
                double[] p = new double[1], e = new double[1], v = new double[1], ev = new double[1], a = new double[1], ea = new double[1];
                Success(ImcApi.IMC_GetAxSts(handle, number, raw), "读取轴状态");
                Success(ImcApi.IMC_GetAxPrfPos(handle, number, p), "读取规划位置");
                Success(ImcApi.IMC_GetAxEncPos(handle, number, e), "读取反馈位置");
                Success(ImcApi.IMC_GetAxPrfVel(handle, number, v), "读取规划速度");
                Success(ImcApi.IMC_GetAxEncVel(handle, number, ev), "读取反馈速度");
                Success(ImcApi.IMC_GetAxPrfAcc(handle, number, a), "读取规划加速度");
                Success(ImcApi.IMC_GetAxEncAcc(handle, number, ea), "读取反馈加速度");
                foreach (double value in new[] { p[0], e[0], v[0], ev[0], a[0], ea[0] }) Finite(value, "轴反馈");
                samples.Add((axis, raw[0], p[0], e[0], v[0], ev[0], a[0], ea[0]));
            }
            // 全部读取成功才提交本批状态；任一失败则使本批状态失效，禁止用旧值准入新动作。
            var now = DateTimeOffset.UtcNow;
            foreach (var sample in samples)
            {
                var state = AxisStates.Single(s => s.Axis == sample.Axis);
                uint raw = unchecked((uint)sample.Raw);
                state.RawStatus = sample.Raw;
                state.Enable = (raw & ImcApi.AX_SVON_BIT) != 0; state.Busy = (raw & ImcApi.AX_BUSY_BIT) != 0;
                state.Arrived = (raw & ImcApi.AX_ARRIVE_BIT) != 0;
                state.Alarm = (raw & (ImcApi.AX_ALARM_BIT | ImcApi.AX_ERRPOS_BIT)) != 0;
                state.PositiveLimit = (raw & (ImcApi.AX_POSLMT_BIT | ImcApi.AX_SOFT_POSLMT_BIT)) != 0;
                state.NegativeLimit = (raw & (ImcApi.AX_NEGLMT_BIT | ImcApi.AX_SOFT_NEGLMT_BIT)) != 0;
                state.EmergencyStop = (raw & ImcApi.AX_EMG_STOP_BIT) != 0;
                // 提供的定义没有回零完成/原点输入位，不能从 Arrived 推导 Homed。
                state.Homed = false; state.Origin = false;
                // Paused 保留基类记录的本会话已接受请求，它不是硬件确认位。
                state.PrfPos = sample.P; state.EncfPos = sample.E; state.PrfVel = sample.V;
                state.EncVel = sample.EV; state.PrfAcc = sample.A; state.EncAcc = sample.EA;
                state.UpdatedAt = now; state.Valid = true;
            }
            return true;
        }
        catch { foreach (var axis in selected) AxisStates.Single(s => s.Axis == axis).Valid = false; throw; }
    }

    protected override bool DoPtpSingleMoveTo(AxisType axis, double pos, AxisSingleConfig config) => Ptp(pos, config, 0);
    protected override bool DoPtpSingleMoveBy(AxisType axis, double pos, AxisSingleConfig config) => Ptp(pos, config, 1);
    private bool Ptp(double pos, AxisSingleConfig config, short relative)
    {
        Finite(pos, "运动目标");
        MotionParameters(config, config.PtpVel, config.PtpAcc, config.PtpDec);
        // 必须使用本次传入目标，不使用配置中的默认 PtpPos。
        Success(ImcApi.IMC_StartPtpMove(Handle(), HardwareAxis(config), pos, relative), relative == 0 ? "请求绝对运动" : "请求相对运动");
        return true;
    }
    protected override bool DoPtpSinglePause(AxisType axis)
    {
        Success(ImcApi.IMC_PauseMove(Handle(), HardwareAxis(AxisConfig(axis))), "请求暂停"); return true;
    }
    protected override bool DoPtpSingleResume(AxisType axis)
    {
        Success(ImcApi.IMC_ResumeMove(Handle(), HardwareAxis(AxisConfig(axis))), "请求恢复"); return true;
    }
    protected override bool DoJogSingleMove(AxisType axis, MoveDirection direction, AxisSingleConfig config)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        MotionParameters(config, config.JogVel, config.JogAcc, config.JogDec);
        Success(ImcApi.IMC_StartJogMove(Handle(), HardwareAxis(config), direction == MoveDirection.正向 ? config.JogVel : -config.JogVel), "请求点动"); return true;
    }
    private void MotionParameters(AxisSingleConfig config, double velocity, double acceleration, double deceleration)
    {
        Positive(velocity, "速度"); Positive(acceleration, "加速度"); Positive(deceleration, "减速度");
        Success(ImcApi.IMC_SetSingleAxMvPara(Handle(), HardwareAxis(config), velocity, acceleration, deceleration), "设置本次运动参数");
    }
    protected override bool DoResetAxisFault(AxisType? axis)
        => throw new NotSupportedException("IMC_ClrAxSts 仅说明清除轴状态，未确认驱动报警复位协议，未下发猜测的复位命令。");

    // XYZ 轴掩码 ABI 和返回码64未确认，不能保留错坐标启动或无界等待，也不能空实现返回成功。
    protected override bool DoGetCrdStatus(CrdType? crd)
    {
        foreach (var state in CrdStates.Where(s => crd is null || s.CrdType == crd)) state.Valid = false;
        throw CoordinateUnsupported();
    }
    protected override bool DoCrdLineMoveTo_XYZ(CrdType crd, List<PosXYZ> points, AxisThreeConfig config) => throw CoordinateUnsupported();
    protected override bool DoCrdPause(CrdType crd) => throw CoordinateUnsupported();
    protected override bool DoCrdResume(CrdType crd) => throw CoordinateUnsupported();
    protected override bool DoCrdClr(CrdType crd) => throw CoordinateUnsupported();
    private static NotSupportedException CoordinateUnsupported() => new("汇川 XYZ 插补 ABI/坐标状态协议尚未确认，暂未开放坐标系创建、启动、暂停、恢复或清除。");

    protected override bool DoInputGetStatus() => ReadIo(true);
    protected override bool DoOutputGetStatus() => ReadIo(false);
    private bool ReadIo(bool input)
    {
        var states = input ? InputStates : OutputStates;
        try
        {
            uint groups = input ? Config.DiNum : Config.DoNum;
            if (groups > 2048) throw new ArgumentOutOfRangeException(nameof(groups), "IO 超过有符号16位地址范围。");
            int count = checked((int)groups);
            if (states.Count != checked(count * 16)) throw new InvalidOperationException("IO 状态点数与配置不一致。");
            if (count == 0) return true;
            short[] words = new short[count];
            Success(input ? ImcApi.IMC_GetEcatPackDi(Handle(), 0, words, checked((short)count)) : ImcApi.IMC_GetEcatPackDo(Handle(), 0, words, checked((short)count)), input ? "读取 DI 组" : "读取 DO 组");
            var now = DateTimeOffset.UtcNow;
            for (int group = 0; group < count; group++)
                for (int bit = 0; bit < 16; bit++)
                {
                    var state = states[group * 16 + bit];
                    if (state.Board != (uint)group || state.Port != bit) throw new InvalidOperationException("IO 映射顺序不一致。");
                    state.Value = DecodeIoBit(words[group], bit);
                    state.UpdatedAt = now; state.Valid = true;
                }
            return true;
        }
        catch { foreach (var state in states) state.Valid = false; throw; }
    }
    protected override bool DoOutputSetStatus(BitStatus point, bool value)
    {
        ArgumentNullException.ThrowIfNull(point);
        short index = OutputIndex(Config.DoNum, point.Board, point.Port);
        Success(ImcApi.IMC_SetEcatDoBit(Handle(), index, value ? (short)1 : (short)0), "请求 DO 输出"); return true;
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
    private AxisSingleConfig AxisConfig(AxisType axis) => Config.AxisSingleConfigs.Single(c => c.Axis == axis);
    private AxisType[] SelectAxes(AxisType? axis)
    {
        if (axis is not null && !Enum.IsDefined(axis.Value)) throw new ArgumentOutOfRangeException(nameof(axis));
        return axis is null ? AxisTypes.ToArray() : [axis.Value];
    }
    private static short HardwareAxis(AxisSingleConfig config)
    {
        if (config.HardwareAxis < 0 || config.HardwareAxis > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(config.HardwareAxis));
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

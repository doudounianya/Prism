using ADT_CARD_632XE;
using Company.Logger;

namespace Company.Hardware.ControlCard.AdTech;

/// <summary>
/// 众为兴控制卡适配。HardwareAxis 使用零基编号，本驱动统一加一转换为 SDK 的 1..32 轴号。
/// 仅在显式初始化后调用原生库；未确认的暂停/插补协议明确拒绝，不以空实现报告成功。
/// </summary>
public sealed class AdtechControlCard : ControlCardBase
{
    private int cardNumber = -1;
    private bool sdkInitialized;
    private readonly Dictionary<AxisType, (int NativeAxis, DateTimeOffset Deadline)> homing = new();
    private readonly HashSet<AxisType> homed = new();
    private AdtechNativeOptions sessionOptions = new();
    /// <summary>现场协议确认选项；只在下一次显式初始化时生成快照，不改变运行中的保护输入语义。</summary>
    public AdtechNativeOptions Options { get; } = new();
    protected override bool RequiresNativeHardware => true;
    public AdtechControlCard() { }

    private AxisSingleConfig AxisConfig(AxisType axis)
    {
        var matches = Config.AxisSingleConfigs.Where(x => x.Axis == axis).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException($"轴 {axis} 必须有且只有一份配置。");
        return matches[0];
    }

    /// <summary>逻辑轴到厂商轴号的唯一转换点；禁止将 X=0 当作全轴编号。</summary>
    private int NativeAxis(AxisType axis)
    {
        var number = AxisConfig(axis).HardwareAxis;
        if (number is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(axis), "AdTech 硬件轴配置必须为零基 0..31。");
        return checked(number + 1);
    }
    private IEnumerable<AxisType> Selection(AxisType? axis) => axis.HasValue ? new[] { axis.Value } : AxisTypes;

    private void Check(int result, string operation)
    {
        if (result != (int)ERR_CODE.ERR_NONE)
            throw new InvalidOperationException($"AdTech 卡 {cardNumber}：{operation}失败，SDK 错误码 {result}。");
    }
    private void RequireOpen()
    {
        if (!sdkInitialized || cardNumber < 0) throw new InvalidOperationException("AdTech 控制卡尚未打开。");
    }

    protected override bool DoInit()
    {
        // 两个开关缺一不可，默认配置不会进入原生 API；解析器注册本身不加载 DLL。
        if (!Config.Enabled || !Config.AllowNativeHardware)
            throw new InvalidOperationException("AdTech 原生硬件未显式启用。");
        sessionOptions = Options.Snapshot();
        if (!sessionOptions.ProtectionInputValuesAreRawLevels.HasValue)
            throw new NotSupportedException("AdTech 保护输入读数含义尚未确认，拒绝加载真实硬件。");
        if (!sessionOptions.AllowBusRestart)
            throw new NotSupportedException("尚未授权停止并重新启动 AdTech 总线；随附 API 未给出既有主站 OP 状态枚举，不能猜测就绪。");
        AdtechNativeLibrary.RegisterResolver();
        var completed = false;
        try
        {
            var result = adt_card_632xe.adt_initial(out var count, 0);
            sdkInitialized = true;
            Check(result, "初始化运动库");
            if (count != 1) throw new InvalidOperationException($"当前适配器只允许单卡，检测到 {count} 张卡。");
            var indexes = new int[32];
            Check(adt_card_632xe.adt_get_card_index(out var actual, indexes), "查询控制卡索引");
            if (actual != 1 || indexes[0] < 0) throw new InvalidOperationException("卡索引查询与单卡初始化结果不一致。");
            cardNumber = indexes[0];
            Check(adt_card_632xe.adt_ecat_stop(cardNumber), "停止原总线通信");
            Check(adt_card_632xe.adt_ecat_load_flash_cfg(cardNumber), "加载卡内总线配置");
            Check(adt_card_632xe.adt_ecat_start(cardNumber), "启动总线通信");
            completed = true;
            return true;
        }
        finally
        {
            if (!completed)
                try { DoClose(); }
                catch (Exception exception) { Logs.LogWarning("AdTech 初始化失败后的资源释放未确认：" + exception.Message); }
        }
    }

    protected override bool DoConfigure()
    {
        RequireOpen();
        var numbers = AxisTypes.Select(NativeAxis).ToArray();
        if (numbers.Distinct().Count() != numbers.Length) throw new InvalidOperationException("AdTech 存在重复物理轴映射。");
        foreach (var logical in AxisTypes)
        {
            var config = AxisConfig(logical);
            var axis = NativeAxis(logical);
            if (!double.IsFinite(config.Equiv) || config.Equiv < 50 || config.Equiv > 25000)
                throw new ArgumentOutOfRangeException(nameof(config.Equiv), "随附 SDK 要求脉冲当量为 50..25000 pulse/mm。");
            if (config.SoftLimitEnabled && (!double.IsFinite(config.SoftLimitNegative) ||
                !double.IsFinite(config.SoftLimitPositive) || config.SoftLimitNegative >= config.SoftLimitPositive))
                throw new ArgumentException($"轴 {logical} 软限位必须是有限且递增的上下界。");
            Check(adt_card_632xe.adt_get_axis_status(cardNumber, axis, out var drive), $"核对轴 {logical} 初始运动状态");
            if (drive != 0) throw new InvalidOperationException($"轴 {logical} 不在静止状态，禁止初始化接管。");
            if (sessionOptions.ApplyAxisConfiguration)
            {
                Check(adt_card_632xe.adt_get_axis_enable(cardNumber, axis, out var enable), $"核对轴 {logical} 使能状态");
                if (enable != 0) throw new InvalidOperationException($"写入轴 {logical} 编程单位/当量前必须关闭使能。");
                Check(adt_card_632xe.adt_set_unit_mode(cardNumber, axis, 0), $"设置轴 {logical} 毫米编程模式");
                Check(adt_card_632xe.adt_set_pulse_equiv(cardNumber, axis, config.Equiv), $"设置轴 {logical} 脉冲当量");
            }
            Check(adt_card_632xe.adt_get_unit_mode(cardNumber, axis, out var mode), $"核对轴 {logical} 编程模式");
            Check(adt_card_632xe.adt_get_pulse_equiv(cardNumber, axis, out var equivalent), $"核对轴 {logical} 脉冲当量");
            if (mode != 0 || !double.IsFinite(equivalent) || Math.Abs(equivalent - config.Equiv) > 1e-6)
                throw new InvalidOperationException($"轴 {logical} 卡上单位/当量与配置不一致；默认不自动覆盖卡配置。");
            if (sessionOptions.ApplySoftLimits)
                Check(adt_card_632xe.adt_set_softlimit_mode(cardNumber, axis, config.SoftLimitEnabled ? 1 : 0,
                    config.SoftLimitPositive, config.SoftLimitNegative, (int)AxisStopMode.减速停止), $"设置轴 {logical} 软限位");
            Check(adt_card_632xe.adt_get_softlimit_mode(cardNumber, axis, out var softEnabled,
                out var positive, out var negative, out _), $"读取轴 {logical} 已有软限位");
            if (softEnabled is < 0 or > 1 || softEnabled == 1 &&
                (!double.IsFinite(positive) || !double.IsFinite(negative) || negative >= positive))
                throw new InvalidOperationException($"轴 {logical} 卡上软限位配置无效。");
            if (config.SoftLimitEnabled && (softEnabled != 1 || Math.Abs(config.SoftLimitPositive - positive) > 1e-6 ||
                Math.Abs(config.SoftLimitNegative - negative) > 1e-6))
                throw new InvalidOperationException($"轴 {logical} 软限位配置与卡上保护不一致，未自动覆盖。");
            // 在本会话的独立配置副本中采用卡上已有保护；默认 false 不能悄悄关闭硬件既有软限位。
            config.SoftLimitEnabled = softEnabled == 1;
            if (softEnabled == 1) { config.SoftLimitPositive = positive; config.SoftLimitNegative = negative; }
        }
        MarkIoAvailability();
        // 急停、原点、硬限位继续使用卡内接线及极性；不以硬编码节点和端口覆盖现场配置。
        // 未确认插补协议时不创建坐标系，也不再传入长度不足的原生轴号数组。
        return true;
    }

    protected override void DoClose()
    {
        if (!sdkInitialized) return;
        var failures = new List<Exception>();
        if (cardNumber >= 0)
        {
            // 释放重试可能发生在基类已清空 Config 之后；只使用本次会话保存的原生卡号/轴号。
            foreach (var request in homing.Values)
            {
                try { Check(adt_card_632xe.adt_stop_servo_home(cardNumber, request.NativeAxis), "关闭时停止伺服回零"); }
                catch (Exception exception) { failures.Add(exception); }
            }
            try { Check(adt_card_632xe.adt_set_axis_stop(cardNumber, 0, (int)AxisStopMode.立即停止), "关闭时停止全部轴"); }
            catch (Exception exception) { failures.Add(exception); }
            try { Check(adt_card_632xe.adt_ecat_stop(cardNumber), "关闭总线通信"); }
            catch (Exception exception) { failures.Add(exception); }
        }
        try
        {
            Check(adt_card_632xe.adt_close_card(), "释放运动库资源");
            sdkInitialized = false;
            cardNumber = -1;
            homing.Clear();
            homed.Clear();
            sessionOptions = new();
        }
        catch (Exception exception) { failures.Add(exception); }
        foreach (var state in AxisStates) state.Valid = false;
        foreach (var state in CrdStates) state.Valid = false;
        foreach (var state in InputStates) state.Valid = false;
        foreach (var state in OutputStates) state.Valid = false;
        // 停止失败也继续释放；全局关闭失败时保留资源标记，以便明确的再次清理。
        if (failures.Count != 0)
        {
            if (sdkInitialized) throw new AggregateException("AdTech 资源释放尚未确认，需再次清理。", failures);
            foreach (var failure in failures)
                Logs.LogWarning("AdTech SDK 资源已释放，但之前的停止/总线关闭未完全确认：" + failure.Message);
        }
    }

    protected override bool DoSetAxisEnable(AxisType? axisType, bool value)
    {
        RequireOpen();
        foreach (var logical in Selection(axisType))
            Check(adt_card_632xe.adt_set_axis_enable(cardNumber, NativeAxis(logical), value ? 1 : 0), $"设置轴 {logical} 使能");
        return true;
    }

    protected override void DoStop(AxisType? axisType, AxisStopMode stopMode)
    {
        RequireOpen();
        var failures = new List<Exception>();
        foreach (var logical in Selection(axisType).Where(homing.ContainsKey).ToArray())
        {
            try
            {
                Check(adt_card_632xe.adt_stop_servo_home(cardNumber, homing[logical].NativeAxis), $"停止轴 {logical} 回零");
                homing.Remove(logical);
                homed.Remove(logical);
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        try
        {
            // SDK 特殊编号 0 只用于显式全轴停止，Stop(X) 必须走映射得到 1。
            Check(adt_card_632xe.adt_set_axis_stop(cardNumber, axisType.HasValue ? NativeAxis(axisType.Value) : 0,
                (int)stopMode), axisType.HasValue ? $"停止轴 {axisType}" : "停止全部轴");
        }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException("AdTech 停止请求未完全接受。", failures);
    }

    protected override bool DoResetAxisFault(AxisType? axisType)
    {
        RequireOpen();
        foreach (var logical in Selection(axisType))
            Check(adt_card_632xe.adt_clear_bus_axis_alarm(cardNumber, NativeAxis(logical)), $"复位轴 {logical} 报警");
        return true;
    }

    private void SetProfile(AxisType logical, double velocity, double acceleration, double deceleration)
    {
        RequireOpen();
        if (!double.IsFinite(velocity) || !double.IsFinite(acceleration) || !double.IsFinite(deceleration) ||
            velocity <= 0 || acceleration <= 0 || deceleration <= 0)
            throw new ArgumentOutOfRangeException(nameof(velocity), "速度、加速度和减速度必须是有限正数。");
        var config = AxisConfig(logical);
        if (velocity * config.Equiv >= 5_000_000 || acceleration * config.Equiv >= 100_000_000 ||
            deceleration * config.Equiv >= 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(velocity), "工程参数乘脉冲当量后超过随附 SDK 上限。");
        Check(adt_card_632xe.adt_set_axis_move_para_unit(cardNumber, NativeAxis(logical), 1, 0,
            velocity, acceleration, deceleration), $"设置轴 {logical} 速度及加减速度");
    }
    protected override bool DoPtpSingleMoveTo(AxisType axisType, double position, AxisSingleConfig config)
    {
        SetProfile(axisType, config.PtpVel, config.PtpAcc, config.PtpDec);
        Check(adt_card_632xe.adt_pmove_unit(cardNumber, NativeAxis(axisType), position, 1), $"轴 {axisType} 绝对定位");
        return true;
    }
    protected override bool DoPtpSingleMoveBy(AxisType axisType, double distance, AxisSingleConfig config)
    {
        SetProfile(axisType, config.PtpVel, config.PtpAcc, config.PtpDec);
        Check(adt_card_632xe.adt_pmove_unit(cardNumber, NativeAxis(axisType), distance, 0), $"轴 {axisType} 相对定位");
        return true;
    }
    protected override bool DoJogSingleMove(AxisType axisType, MoveDirection direction, AxisSingleConfig config)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        SetProfile(axisType, config.JogVel, config.JogAcc, config.JogDec);
        // 正向=0、反向=1 与随附 SDK 一致，但轴号不能直接强转。
        Check(adt_card_632xe.adt_continue_move(cardNumber, NativeAxis(axisType), (int)direction), $"轴 {axisType} 点动");
        return true;
    }

    private static int HomePulses(uint engineeringValue, double equivalent)
    {
        var pulses = engineeringValue * equivalent;
        if (!double.IsFinite(pulses) || pulses <= 0 || pulses > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(engineeringValue), "回零参数转换为脉冲单位后超出 Int32 正数范围。");
        return checked((int)Math.Round(pulses, MidpointRounding.AwayFromZero));
    }
    protected override bool DoGoHome(AxisType axisType, AxisSingleConfig config)
    {
        RequireOpen();
        if (config.HomeMethod is < 1 or > 35 || config.HomeOvertime == 0)
            throw new ArgumentOutOfRangeException(nameof(config.HomeMethod), "回零方式为 1..35，超时必须大于零。");
        var axis = NativeAxis(axisType);
        Check(adt_card_632xe.adt_set_servo_home_para(cardNumber, axis, 0,
            HomePulses(config.HomehighVel, config.Equiv), HomePulses(config.HomelowVel, config.Equiv),
            HomePulses(config.HomeAcc, config.Equiv)), $"设置轴 {axisType} 回零参数");
        homed.Remove(axisType);
        Check(adt_card_632xe.adt_set_servo_home(cardNumber, axis, config.HomeMethod), $"启动轴 {axisType} 回零");
        homing[axisType] = (axis, DateTimeOffset.UtcNow.AddMilliseconds(config.HomeOvertime));
        return true;
    }

    /// <summary>按卡内既有 IO 映射和有效电平解读保护输入，不把历史停止原因当作实时输入。</summary>
    private bool SignalActive(int axis, int mode)
    {
        var rawMeaning = sessionOptions.ProtectionInputValuesAreRawLevels;
        if (!rawMeaning.HasValue)
            throw new NotSupportedException("保护输入语义未确认，不能推断保护回路正常。");
        Check(adt_card_632xe.adt_get_hardlimit_mode(cardNumber, axis, mode, out var enabled, out var logic, out _), "读取保护信号极性");
        if (enabled == 0) return false;
        if (enabled != 1 || logic is < 0 or > 1) throw new InvalidOperationException("保护信号配置无效。");
        Check(adt_card_632xe.adt_get_axis_io_map(cardNumber, axis, mode, out var board, out var port), "读取保护信号映射");
        if (port is < 0 or > 15) throw new InvalidOperationException("保护信号端口超出 SDK 映射范围。");
        int raw;
        if (board == 0)
        {
            if (mode == 4) throw new NotSupportedException("全局急停映射到伺服自身 IO 的轴归属未确认，不能推断正常。");
            Check(adt_card_632xe.adt_read_servo_inbit(cardNumber, axis, port, out raw), "读取伺服保护输入");
        }
        else if (board is >= 1 and <= 16)
            Check(adt_card_632xe.adt_read_busio_inbit(cardNumber, board, port, out raw), "读取总线保护输入");
        else throw new InvalidOperationException("保护输入节点超出 SDK 映射范围。");
        if (raw is < 0 or > 1) throw new InvalidOperationException("保护输入不是有效二进制读数。");
        return rawMeaning.Value ? raw == logic : raw == 1;
    }

    protected override bool DoGetAxisStatus(AxisType? axisType)
    {
        RequireOpen();
        foreach (var logical in Selection(axisType))
        {
            var state = AxisStates.Single(x => x.Axis == logical);
            state.Valid = false;
            var config = AxisConfig(logical);
            var axis = NativeAxis(logical);
            Check(adt_card_632xe.adt_get_axis_status(cardNumber, axis, out var drive), $"读取轴 {logical} 驱动状态");
            if (drive is < 0 or > 2) throw new InvalidOperationException("SDK 返回未知的轴运动状态。");
            Check(adt_card_632xe.adt_get_axis_enable(cardNumber, axis, out var enable), $"读取轴 {logical} 使能");
            if (enable is < 0 or > 1) throw new InvalidOperationException("SDK 返回未知的轴使能状态。");
            Check(adt_card_632xe.adt_get_axis_alarm_info(cardNumber, axis, out var alarm), $"读取轴 {logical} 报警");
            Check(adt_card_632xe.adt_get_command_pos(cardNumber, axis, out var planned), $"读取轴 {logical} 规划位置");
            Check(adt_card_632xe.adt_get_actual_pos(cardNumber, axis, out var actual), $"读取轴 {logical} 反馈位置");
            Check(adt_card_632xe.adt_get_speed(cardNumber, axis, out var speed), $"读取轴 {logical} 规划速度");
            Check(adt_card_632xe.adt_get_encoder_speed(cardNumber, axis, out var encoderSpeed), $"读取轴 {logical} 反馈速度");
            var positive = SignalActive(axis, 0);
            var negative = SignalActive(axis, 1);
            var origin = SignalActive(axis, 2);
            var emergency = SignalActive(0, 4);
            var feedbackVelocity = encoderSpeed * 1000 / config.Equiv;
            if (!double.IsFinite(speed) || !double.IsFinite(feedbackVelocity)) throw new InvalidOperationException("轴速度读数或单位转换结果不是有限数。");
            if (homing.TryGetValue(logical, out var homeRequest))
            {
                var home = adt_card_632xe.adt_get_servo_home_status(cardNumber, axis);
                if (home == 0) { homing.Remove(logical); homed.Add(logical); }
                else if (home != 1) throw new InvalidOperationException($"轴 {logical} 回零失败，状态 {home}。");
                else if (DateTimeOffset.UtcNow >= homeRequest.Deadline)
                {
                    // 只在状态采样时观测超时并尽力停止；没有独立硬件看门狗的保证。
                    DoStop(logical, AxisStopMode.立即停止);
                    throw new TimeoutException($"轴 {logical} 回零超时，已提交停止请求。");
                }
            }
            state.Enable = enable == 1;
            state.Busy = drive == 1 || homing.ContainsKey(logical);
            state.Paused = drive == 2;
            state.Alarm = alarm != 0;
            state.PrfPos = planned / config.Equiv; // 整数位置为 pulse。
            state.EncfPos = actual / config.Equiv;
            state.PrfVel = speed; // 已配置 mm 模式，规划速度为 mm/s。
            state.EncVel = feedbackVelocity; // 编码器速度为 pulse/ms，转换结果已经检查。
            state.Homed = homed.Contains(logical);
            state.Arrived = false; // 未确认到位检测已启用，不能用“静止”伪造到位。
            state.PositiveLimit = positive;
            state.NegativeLimit = negative;
            state.Origin = origin;
            state.EmergencyStop = emergency;
            state.RawStatus = drive;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            state.Valid = true;
        }
        return true;
    }

    private void MarkIoAvailability()
    {
        foreach (var point in InputStates.Concat(OutputStates))
        {
            // 公共组号在本驱动代表 SDK 节点：0 为板卡本地 8 点，1..32 为扩展节点。
            point.Available = point.Board <= 32 && point.Port >= 0 && point.Port <= (point.Board == 0 ? 7 : 15);
            if (!point.Available) point.Valid = false;
        }
    }

    private static void ValidateIo(BitStatus point)
    {
        if (point.Board > 32 || point.Port < 0 || point.Port > (point.Board == 0 ? 7 : 15))
            throw new ArgumentOutOfRangeException(nameof(point), "本地节点 0 仅支持点 0..7，扩展节点 1..32 支持点 0..15。");
    }
    private bool ReadIo(bool output)
    {
        RequireOpen();
        MarkIoAvailability();
        var states = output ? OutputStates : InputStates;
        foreach (var state in states) state.Valid = false;
        var available = states.Where(x => x.Available).ToArray();
        var values = new List<bool>();
        foreach (var state in available)
        {
            ValidateIo(state);
            int raw;
            int result;
            if (state.Board == 0)
                result = output ? adt_card_632xe.adt_read_outbit(cardNumber, state.Port, out raw)
                    : adt_card_632xe.adt_read_inbit(cardNumber, state.Port, out raw);
            else
                result = output ? adt_card_632xe.adt_read_busio_outbit(cardNumber, checked((int)state.Board), state.Port, out raw)
                    : adt_card_632xe.adt_read_busio_inbit(cardNumber, checked((int)state.Board), state.Port, out raw);
            Check(result, output ? "读取输出点" : "读取输入点");
            if (raw is < 0 or > 1) throw new InvalidOperationException("IO 值不是有效二进制状态。");
            values.Add(raw == 1);
        }
        for (var i = 0; i < available.Length; i++)
        {
            available[i].Value = values[i];
            available[i].UpdatedAt = DateTimeOffset.UtcNow;
            available[i].Valid = true;
        }
        return true;
    }
    protected override bool DoInputGetStatus() => ReadIo(false);
    protected override bool DoOutputGetStatus() => ReadIo(true);
    protected override bool DoOutputSetStatus(BitStatus point, bool value)
    {
        RequireOpen();
        MarkIoAvailability();
        ValidateIo(point);
        if (!OutputStates.Any(x => x.Board == point.Board && x.Port == point.Port && x.Available))
            throw new ArgumentOutOfRangeException(nameof(point), "该 AdTech 输出点未配置或不存在。");
        var result = point.Board == 0
            ? adt_card_632xe.adt_write_outbit(cardNumber, point.Port, value ? 1 : 0)
            : adt_card_632xe.adt_write_busio_outbit(cardNumber, checked((int)point.Board), point.Port, value ? 1 : 0);
        Check(result, "设置输出点");
        return true;
    }

    protected override bool DoPtpSinglePause(AxisType axisType) => throw new NotSupportedException("AdTech 独立 PTP 暂停协议未确认，不以修改倍率模拟暂停。");
    protected override bool DoPtpSingleResume(AxisType axisType) => throw new NotSupportedException("AdTech 独立 PTP 暂停恢复协议未确认。");
    protected override bool DoGetCrdStatus(CrdType? crdType)
    {
        foreach (var state in CrdStates) state.Valid = false;
        throw new NotSupportedException("AdTech 插补协议未完成确认，当前不提供有效坐标系状态。");
    }
    protected override bool DoCrdLineMoveTo_XYZ(CrdType crdType, List<PosXYZ> positions, AxisThreeConfig config) => throw new NotSupportedException("AdTech 插补 FIFO 启动/清空协议未确认，当前不提交轨迹。");
    protected override bool DoCrdPause(CrdType crdType) => throw new NotSupportedException("AdTech 插补协议尚未启用。");
    protected override bool DoCrdResume(CrdType crdType) => throw new NotSupportedException("AdTech 插补协议尚未启用。");
    protected override bool DoCrdClr(CrdType crdType) => throw new NotSupportedException("AdTech 插补 FIFO 清空协议未确认。");
}

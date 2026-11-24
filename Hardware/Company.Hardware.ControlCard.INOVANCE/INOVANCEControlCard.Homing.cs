using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;

namespace Company.Hardware.ControlCard.INOVANCE;

public sealed partial class INOVANCEControlCard
{
    /// <summary>
    /// 回零请求与完成分开。句柄、物理轴号、时限均在请求时冻结，后台任务不引用可能失效的Config。
    /// SDK的overtime在手册中被标为暂时无效，所以必须由独立时钟监督。
    /// </summary>
    private sealed class HomeSession(AxisId axis, short number, ulong handle, long started, int timeout, int poll, short initialStatus)
    {
        internal readonly AxisId Axis = axis;
        internal readonly short Number = number;
        internal readonly ulong Handle = handle;
        internal readonly long Started = started;
        internal readonly int TimeoutMs = timeout;
        internal readonly int PollMs = poll;
        internal readonly short InitialStatus = initialStatus;
        internal readonly CancellationTokenSource Lifetime = new();
        internal long? CleanupStarted;
        internal bool StopRequested;
        internal bool StopSent;
        internal bool SawProgress;
        internal bool SawStatusTransition;
        internal bool ExitRequested;
        internal bool ExitSuccess;
        internal bool MonitorAbandoned;
    }
    private readonly Dictionary<AxisId, HomeSession> homeSessions = [];
    private readonly Dictionary<AxisId, (int Code, string Message)> homeFaults = [];

    protected override bool StartHomingCore(AxisId axisId, AxisConfiguration config)
    {
        if (!options.HomingAxisUnitsPerSecondConfirmed)
            throw new NotSupportedException("回零单位未确认；确认轴单位/s、轴单位/s²后才能启用InUnit回零。");
        Positive(config.HomingSearchVelocity, "回零高速"); Positive(config.HomingApproachVelocity, "回零低速"); Positive(config.HomingAcceleration, "回零加速度");
        if (config.HomingApproachVelocity > config.HomingSearchVelocity || config.HomingTimeoutMs is 0 or > 300000)
            throw new ArgumentException("回零速度或独立超时配置不合法（时限1至300000ms）。");
        // 此入口只实现标准位置回零，不能把负数转矩回零模式套用同一组状态及收尾协议。
        if (config.HomingMethod is not (>= 1 and <= 14) and not (>= 17 and <= 30) and not (>= 33 and <= 35))
            throw new NotSupportedException("当前标准回零入口未适配该回零方法。");
        if (homeSessions.ContainsKey(axisId)) throw new InvalidOperationException("上一回零仍未退出。");
        ulong handle = Handle(); short number = HardwareAxis(config);
        ValidateCurrentBinding(handle, axisId);
        short initialStatus = ImcApi.HOME_INTERRUPTED_OR_NOT_START;
        Success(api.IMC_GetHomingStatus(handle, number, ref initialStatus), "核对回零前的独立状态");
        if (initialStatus is < 0 or > 5) throw new InvalidOperationException("回零前状态未知。");
        var session = new HomeSession(axisId, number, handle, clock.GetTimestamp(), checked((int)config.HomingTimeoutMs), Configuration.PollIntervalMs, initialStatus);
        var state = AxisStateBuffer.Single(s => s.Axis == axisId);
        homeSessions.Add(axisId, session);
        homeFaults.Remove(axisId);
        state.Homed = false; state.Homing = true; state.FaultCode = 0; state.ErrorMessage = string.Empty;
        ImcApi.THomingParaInUint parameters = new()
        {
            homeMethod = config.HomingMethod, offset = 0, highVel = config.HomingSearchVelocity,
            lowVel = config.HomingApproachVelocity, acc = config.HomingAcceleration, overtime = config.HomingTimeoutMs,
            posSrc = axisBindings[axisId].AxisId == 0 ? axisBindings[axisId].LocalEncoderSource : (short)0
        };
        try
        {
            Success(api.IMC_StartHomingInUnit(handle, number, ref parameters), "请求回零");
        }
        catch (Exception ex)
        {
            // 不确定是否部分下发时，保留会话并请求停机退出，不能直接忘记回零资源。
            FaultHome(session, -3, "回零启动未确认：" + ex.Message);
            _ = MonitorHomeAsync(session);
            throw;
        }
        _ = MonitorHomeAsync(session);
        return true;
    }

    private async Task MonitorHomeAsync(HomeSession session)
    {
        try
        {
            // 首次等待也在后台进行，GoHome只返回指令是否接受，不在UI线程等到位。
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(session.PollMs), clock, session.Lifetime.Token).ConfigureAwait(false);
                bool keep = Synchronize(() =>
                {
                    if (!IsCurrentHome(session) || cardHandle != session.Handle) return false;
                    ObserveHome(session.Axis);
                    return IsCurrentHome(session) && !session.MonitorAbandoned;
                });
                if (!keep) return;
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // 读取或停止异常只记录故障和保留责任；不能让未观察的后台异常退出后解锁。
            bool retry = Synchronize(() =>
            {
                if (IsCurrentHome(session))
                {
                    FaultHome(session, -3, "回零监督失败：" + ex.Message);
                    try
                    {
                        // 一次读取失败不代表运动已经停止，仍尽力提交停止；未知状态不执行Finish或释放资源。
                        if (!session.StopSent)
                        {
                            Success(api.IMC_AxMoveStop(session.Handle, session.Number, (short)StopMode.Decelerate), "监督异常后停止回零轴");
                            session.StopSent = true;
                        }
                    }
                    catch (Exception stopException)
                    {
                        AxisStateBuffer.Single(s => s.Axis == session.Axis).ErrorMessage += " 停止未确认：" + stopException.Message;
                    }
                    session.MonitorAbandoned = clock.GetElapsedTime(session.CleanupStarted!.Value).TotalMilliseconds >= Math.Min(session.TimeoutMs, 30000);
                    return !session.MonitorAbandoned;
                }
                return false;
            });
            // 故障恢复也只能有限重试，超出时限仍保留资源，等待明确停止/关闭重试。
            if (retry) _ = MonitorHomeAsync(session);
        }
    }

    private bool IsCurrentHome(HomeSession session)
        => homeSessions.TryGetValue(session.Axis, out var current) && ReferenceEquals(current, session);

    private void FaultHome(HomeSession session, int code, string message)
    {
        session.StopRequested = true;
        session.CleanupStarted ??= clock.GetTimestamp();
        homeFaults[session.Axis] = (code, message);
        var state = AxisStateBuffer.Single(s => s.Axis == session.Axis);
        state.Homed = false; state.Homing = true; state.FaultCode = code; state.ErrorMessage = message;
    }

    /// <summary>仅执行一轮有界调用；由状态读取和后台监督共用同一SDK串行锁。</summary>
    private void ObserveHome(AxisId axis)
    {
        if (!homeSessions.TryGetValue(axis, out var session)) return;
        if (cardHandle != session.Handle || cardHandle == 0)
            throw new InvalidOperationException("回零句柄会话已经变化。");
        if (!session.StopRequested && clock.GetElapsedTime(session.Started).TotalMilliseconds >= session.TimeoutMs)
            FaultHome(session, -1, "回零独立监督超时；已经请求停止，仍须确认退出回零。");

        int[] raw = new int[1];
        Success(api.IMC_GetAxSts(session.Handle, session.Number, raw), "读取回零轴状态");
        uint status = unchecked((uint)raw[0]);
        bool busy = (status & ImcApi.AX_BUSY_BIT) != 0;
        bool fault = (status & (ImcApi.AX_ALARM_BIT | ImcApi.AX_ERRPOS_BIT | ImcApi.AX_SW_ABNOR_BIT | ImcApi.AX_EMG_STOP_BIT)) != 0;
        if (fault && !session.StopRequested) FaultHome(session, -2, "回零期间轴报警或急停，必须退出回零模式。");

        if (session.ExitRequested && !session.StopRequested)
        {
            CompleteHome(session, session.ExitSuccess);
            return;
        }
        short homing = ImcApi.HOME_INTERRUPTED_OR_NOT_START;
        Success(api.IMC_GetHomingStatus(session.Handle, session.Number, ref homing), "读取独立回零状态");
        if (homing is < 0 or > 5) throw new InvalidOperationException("回零状态值未知。");
        if (homing == ImcApi.HOME_IN_PROGRESS) session.SawProgress = true;
        if (homing != session.InitialStatus) session.SawStatusTransition = true;
        if (!session.StopRequested)
        {
            if (homing is ImcApi.HOME_ERR_VEL_NOT_ZERO or ImcApi.HOME_ERR_VEL_ZERO)
                FaultHome(session, homing, "控制器报告回零错误，正在停止退出。");
            else if (homing == ImcApi.HOME_INTERRUPTED_OR_NOT_START &&
                (session.SawProgress || clock.GetElapsedTime(session.Started).TotalMilliseconds >= Math.Max(2 * session.PollMs, 50)))
                FaultHome(session, -2, "回零被中断或未确认启动，正在停止退出。");
        }

        if (session.StopRequested)
        {
            session.CleanupStarted ??= clock.GetTimestamp();
            if (!session.StopSent)
            {
                Success(api.IMC_AxMoveStop(session.Handle, session.Number, (short)StopMode.Decelerate), "停止回零轴");
                session.StopSent = true;
                // 停止接受后重新读一次，不以停止前的旧Busy=false证明已经停稳。
                Success(api.IMC_GetAxSts(session.Handle, session.Number, raw), "确认回零停止状态");
                busy = (unchecked((uint)raw[0]) & ImcApi.AX_BUSY_BIT) != 0;
            }
            if (!busy) CompleteHome(session, false);
            else if (clock.GetElapsedTime(session.CleanupStarted.Value).TotalMilliseconds >= Math.Min(session.TimeoutMs, 30000))
            {
                session.MonitorAbandoned = true;
                var state = AxisStateBuffer.Single(s => s.Axis == axis);
                const string retained = " 停止尚未确认，继续保留占用，请重试停止或关闭。";
                if (!state.ErrorMessage.Contains(retained, StringComparison.Ordinal)) state.ErrorMessage += retained;
            }
            return;
        }
        bool currentCycle = session.InitialStatus != ImcApi.HOME_SUCESS || session.SawProgress || session.SawStatusTransition;
        // 状态3后先退出回零，再读取轴空闲；部分驱动在Finish前仍保持Busy。
        // 启动前已经是3、之后一直是3的旧反馈不能冒充本次成功。
        if (homing == ImcApi.HOME_SUCESS && currentCycle && !fault) CompleteHome(session, true);
        // 状态2只是结束但尚未到目标，继续等3或独立超时，绝不提前发布Homed。
    }

    private void CompleteHome(HomeSession session, bool success)
    {
        if (!session.ExitRequested)
        {
            Success(api.IMC_FinishHoming(session.Handle, session.Number), "退出回零模式");
            session.ExitRequested = true;
            session.ExitSuccess = success;
        }
        else if (!success) session.ExitSuccess = false;
        int[] raw = new int[1];
        Success(api.IMC_GetAxSts(session.Handle, session.Number, raw), "确认退出回零后的轴状态");
        uint status = unchecked((uint)raw[0]);
        if ((status & ImcApi.AX_BUSY_BIT) != 0) return;
        bool clear = (status & (ImcApi.AX_ALARM_BIT | ImcApi.AX_ERRPOS_BIT | ImcApi.AX_SW_ABNOR_BIT | ImcApi.AX_EMG_STOP_BIT)) == 0;
        var state = AxisStateBuffer.Single(s => s.Axis == session.Axis);
        state.Homed = session.ExitSuccess && clear; state.Homing = false;
        state.UpdatedAt = CurrentTime;
        homeSessions.Remove(session.Axis);
        session.Lifetime.Cancel();
        // 故障诊断不会因Finish成功消失；明确停止/复位可在确认无物理故障后清除。
    }

    private void RequestHomeStops(AxisId? axis, StopMode mode)
    {
        List<string> failures = [];
        foreach (var session in homeSessions.Values.Where(s => axis is null || s.Axis == axis.Value).ToArray())
        {
            session.StopRequested = true; session.CleanupStarted ??= clock.GetTimestamp();
            try
            {
                Success(api.IMC_AxMoveStop(session.Handle, session.Number, (short)mode), "请求停止回零");
                session.StopSent = true;
                ObserveHome(session.Axis);
            }
            catch (Exception ex) { FaultHome(session, -3, "回零停止/退出未确认：" + ex.Message); failures.Add(ex.Message); }
        }
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("；", failures));
    }

    private void CloseHomes()
    {
        foreach (var session in homeSessions.Values) session.Lifetime.Cancel();
        RequestHomeStops(null, StopMode.Decelerate);
        if (homeSessions.Count != 0) throw new InvalidOperationException("仍有回零未确认退出，暂不释放控制卡句柄，请重试关闭。");
        homeFaults.Clear();
    }
}

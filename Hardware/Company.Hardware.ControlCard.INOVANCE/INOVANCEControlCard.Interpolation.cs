using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>
/// IMC 普通插补协议。只管理本驱动创建的坐标系，不接管已有坐标系或输出事件。
/// SDK调用沿用基类串行边界；后台搬运每步有上限，等待期间不占用调用锁。
/// </summary>
public sealed partial class INOVANCEControlCard
{
    private sealed class CoordinateSession(CoordinateId type, short[] mapping, AxisId[] axes, CoordinateConfiguration configuration, int pollInterval, int timeout)
    {
        internal readonly CoordinateId Type = type;
        internal readonly short[] Mapping = mapping;
        internal readonly AxisId[] Axes = axes;
        internal readonly CoordinateConfiguration Configuration = configuration;
        internal readonly int PollInterval = pollInterval;
        internal readonly int Timeout = timeout;
        internal bool Owned;
        internal bool Prepared;
        internal bool HasJob;
        internal bool Enabled;
        internal bool Paused;
        internal bool PausePositionKnown;
        internal double[] PausePosition = new double[3];
        internal long PauseStarted;
        internal bool CancelRequested;
        internal bool StopIssued;
        internal bool Faulted;
        internal bool FaultStopAttempted;
        internal bool Completed;
        internal bool Cleared;
        internal bool Started;
        internal bool FlushComplete;
        internal bool CompletionStopRequested;
        internal bool ObservedProgress;
        internal bool AnyAxisBusy;
        internal bool NoOp;
        internal bool PumpRunning;
        internal int Generation;
        internal int CancellationSerial;
        internal int Next;
        internal int Raw;
        internal double LastVelocity = double.NaN;
        internal double LastAcceleration = double.NaN;
        internal int FaultCode;
        internal string Error = string.Empty;
        internal long DeadlineStarted;
        internal TimeSpan DeadlineBudget;
        internal TrajectorySegment[] Segments = [];
        internal double[] InitialPosition = new double[3];
        internal double[] FinalPosition = new double[3];
        internal StopMode StopMode;
    }

    private readonly Dictionary<CoordinateId, CoordinateSession> coordinateSessions = [];

    private short CoordinateNumber(CoordinateId coordinate)
    {
        int number = Configuration.Coordinates.Single(c => c.CoordinateId == coordinate).HardwareCoordinate;
        if (!coordinate.IsValid || number is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(coordinate), "IMC坐标系编号必须为0至3。");
        return checked((short)number);
    }

    private CoordinateState CoordinateState(CoordinateId coordinate) => CoordinateStateBuffer.Single(x => x.CoordinateId == coordinate);
    private CoordinateSession OwnedCoordinate(CoordinateId coordinate)
        => coordinateSessions.TryGetValue(coordinate, out var session) && session.Owned
            ? session : throw new InvalidOperationException("本会话未创建此坐标系，拒绝修改其他程序的队列。");

    protected override bool DoPrepareCoordinate(CoordinateId coordinate, CoordinateConfiguration config)
    {
        if (!options.InterpolationProtocolConfirmed)
            throw new NotSupportedException("插补协议与设备配置尚未确认，不能自动开放真实轨迹执行。");
        short number = CoordinateNumber(coordinate);
        if (!config.CoordinateEnabled || config.LookAheadSegments is < 0 or > 10000)
            throw new ArgumentException("坐标系未启用或前瞻段数超出本驱动支持范围。");
        if (!double.IsFinite(config.TransitionTolerance) || config.TransitionTolerance <= 0 || config.TransitionTolerance > 1e9 ||
            !double.IsFinite(config.ArrivalTolerance) || config.ArrivalTolerance <= 0)
            throw new ArgumentException("轨迹过渡精度或到位判定精度无效。");
        ValidateCoordinateSpeed(config.LinearVelocity, "直线速度");
        ValidateCoordinateSpeed(config.LinearAcceleration, "直线加速度");
        ValidateCoordinateSpeed(config.LinearDeceleration, "轨迹减速度");
        ValidateCoordinateSpeed(config.ArcVelocity, "圆弧速度");
        ValidateCoordinateSpeed(config.ArcAcceleration, "圆弧加速度");
        AxisId[] axes = [config.XAxis, config.YAxis, config.ZAxis];
        short[] mapping = axes.Select(axis => HardwareAxis(AxisConfig(axis))).ToArray();
        if (mapping.Distinct().Count() != 3 || mapping.Any(x => x is < 0 or > 31))
            throw new ArgumentException("IMC插补映射必须为三个互异的有效物理轴号。");

        if (!coordinateSessions.TryGetValue(coordinate, out var session) || !session.Owned)
        {
            coordinateSessions.Remove(coordinate);
            short[] existing = new short[3]; short lookAhead = 0; double stopDec = 0;
            uint result = api.IMC_CrdGetMtSysParam(Handle(), number, existing, ref lookAhead, ref stopDec);
            // 只有明确的“坐标系不存在”才可以创建。已有坐标系即使映射相同也不能取得所有权。
            if (result == ImcApi.EXE_SUCCESS)
                throw new NotSupportedException("该IMC坐标系已经存在，未覆盖其映射或接管外部队列。");
            if (result != ImcApi.ERR_CRD_NOT_EXIST) Success(result, "确认插补坐标系未被占用");
            session = new CoordinateSession(coordinate, mapping, axes, config.Snapshot(), Configuration.PollIntervalMs, Configuration.CommandTimeoutMs) { Owned = true };
            coordinateSessions.Add(coordinate, session);
            try
            {
                // 先登记清理责任；若SDK创建发生不确定异常，不丢弃可能存在的坐标系资源。
                Success(api.IMC_CrdSetMtSys(Handle(), number, mapping, checked((short)config.LookAheadSegments), config.LinearDeceleration), "创建插补坐标系");
            }
            catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: false); throw new InvalidOperationException("创建坐标系未确认，需明确清理。", ex); }
        }
        else if (!session.Mapping.SequenceEqual(mapping))
            throw new InvalidOperationException("本会话坐标映射已冻结，不能在未关闭会话时重绑轴。");

        try
        {
            if (session.Faulted || session.Paused || session.Enabled || session.HasJob && !session.Completed && !session.Cleared)
                throw new InvalidOperationException("坐标系仍有运行、暂停或故障任务，需先完成或清理。");
            VerifyCoordinateMapping(session);
            var sample = ReadCoordinateRaw(session);
            if (sample.Busy || sample.Fault != 0) throw new InvalidOperationException("准备坐标系时读取到运动或故障。");
            Success(api.IMC_CrdSetIncMode(Handle(), number, 0), "设置绝对终点编程");
            short mode = -1;
            Success(api.IMC_CrdGetIncMode(Handle(), number, ref mode), "核对终点编程方式");
            if (mode != 0) throw new InvalidOperationException("IMC未确认绝对终点编程方式。");
            ImcApi.TCrdAdvParam requested = new()
            {
                userVelMode = 0, transMode = 1, noDataProtect = 1,
                circAccChangeEn = 0, noCoplaneCircOptm = 0,
                turnCoef = 1, tol = config.TransitionTolerance
            };
            Success(api.IMC_CrdSetAdvParam(Handle(), number, ref requested), "设置前瞻、过渡和断流保护");
            ImcApi.TCrdAdvParam actual = new();
            Success(api.IMC_CrdGetAdvParam(Handle(), number, ref actual), "核对插补高级参数");
            if (actual.userVelMode != requested.userVelMode || actual.transMode != requested.transMode ||
                actual.noDataProtect != 1 || actual.circAccChangeEn != 0 || actual.noCoplaneCircOptm != 0 ||
                !SameParameter(actual.turnCoef, 1) || !SameParameter(actual.tol, config.TransitionTolerance))
                throw new InvalidOperationException("IMC高级参数回读不一致。");
            Success(api.IMC_CrdSetRatio(Handle(), number, 1), "设置明确的轨迹倍率");
            double ratio = 0;
            Success(api.IMC_CrdGetRatio(Handle(), number, ref ratio), "核对轨迹倍率");
            if (!SameParameter(ratio, 1)) throw new InvalidOperationException("IMC轨迹倍率回读不一致。");
            Success(api.IMC_CrdSetZeroFlag(Handle(), number, 0), "关闭强制段末零速标记");
            session.LastVelocity = double.NaN; session.LastAcceleration = double.NaN;
            SetCoordinateMotionParameters(session, config.LinearVelocity, config.LinearAcceleration);
            session.Prepared = true;
            PublishCoordinate(session);
            return true;
        }
        catch (Exception ex)
        {
            MarkCoordinateFault(session, ex, tryStop: false);
            throw new InvalidOperationException("插补准备失败，坐标系清理责任仍保留。", ex);
        }
    }

    private static void ValidateCoordinateSpeed(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 1e-6 or > 1e9) throw new ArgumentOutOfRangeException(name, name + "须在1e-6至1e9之间。");
    }
    private static bool SameParameter(double actual, double expected)
        => double.IsFinite(actual) && Math.Abs(actual - expected) <= Math.Max(1, Math.Abs(expected)) * 1e-9;

    private void VerifyCoordinateMapping(CoordinateSession session)
    {
        if (!VerifyCoordinateOwnership(session, allowMissing: false))
            throw new InvalidOperationException("本会话插补坐标系已不存在。");
    }

    /// <summary>控制器可能被外部程序重绑；每次任务步进和启停前重新核对，拒绝作用于未知队列。</summary>
    private bool VerifyCoordinateOwnership(CoordinateSession session, bool allowMissing)
    {
        short[] actual = new short[3]; short count = 0; double deceleration = 0;
        uint result = api.IMC_CrdGetMtSysParam(Handle(), CoordinateNumber(session.Type), actual, ref count, ref deceleration);
        if (result == ImcApi.ERR_CRD_NOT_EXIST && allowMissing) return false;
        Success(result, "核对插补映射与缓冲参数");
        if (!actual.SequenceEqual(session.Mapping) || count != session.Configuration.LookAheadSegments ||
            !SameParameter(deceleration, session.Configuration.LinearDeceleration))
            throw new InvalidOperationException("IMC坐标映射、前瞻段数或停止减速度不一致。");
        foreach (var axis in session.Axes) ValidateCurrentBinding(Handle(), axis);
        return true;
    }

    private void SetCoordinateMotionParameters(CoordinateSession session, double velocity, double acceleration)
    {
        if (SameParameter(session.LastVelocity, velocity) && SameParameter(session.LastAcceleration, acceleration)) return;
        short number = CoordinateNumber(session.Type);
        Success(api.IMC_CrdSetTrajVel(Handle(), number, velocity), "设置轨迹速度");
        Success(api.IMC_CrdSetTrajAccAndDec(Handle(), number, acceleration, session.Configuration.LinearDeceleration), "设置轨迹加减速度");
        double v = 0, a = 0, d = 0;
        Success(api.IMC_CrdGetTrajVel(Handle(), number, ref v), "核对轨迹速度");
        Success(api.IMC_CrdGetTrajAccAndDec(Handle(), number, ref a, ref d), "核对轨迹加减速度");
        if (!SameParameter(v, velocity) || !SameParameter(a, acceleration) || !SameParameter(d, session.Configuration.LinearDeceleration))
            throw new InvalidOperationException("IMC轨迹速度或加减速度回读不一致。");
        session.LastVelocity = velocity; session.LastAcceleration = acceleration;
    }

    protected override bool SubmitTrajectoryCore(CoordinateId coordinate, IReadOnlyList<TrajectorySegment> segments, CoordinateConfiguration config)
    {
        var session = OwnedCoordinate(coordinate);
        if (!session.Prepared || session.Faulted || session.Enabled || session.Paused || session.HasJob && !session.Completed && !session.Cleared)
            throw new InvalidOperationException("坐标系尚未准备好或上一任务没有结束。");
        if (segments.Count is < 1 or > 10000) throw new ArgumentException("轨迹段数量无效。");
        session.Segments = segments.ToArray();
        session.InitialPosition = ReadCoordinateAxes(session);
        var last = session.Segments[^1]; session.FinalPosition = [last.X, last.Y, last.Z];
        session.NoOp = session.Segments.All(x => x.Kind == TrajectorySegmentKind.Line &&
            Math.Abs(x.X - session.InitialPosition[0]) <= config.ArrivalTolerance &&
            Math.Abs(x.Y - session.InitialPosition[1]) <= config.ArrivalTolerance &&
            Math.Abs(x.Z - session.InitialPosition[2]) <= config.ArrivalTolerance);
        // 上一代泵可能已发布完成但尚未执行finally；新代必须独立调度，旧代不得覆盖新代标记。
        session.Generation++; session.PumpRunning = false; session.Next = 0; session.HasJob = true;
        session.Started = false; session.FlushComplete = false; session.ObservedProgress = false; session.CompletionStopRequested = false;
        session.Completed = false; session.Cleared = false; session.CancelRequested = false; session.StopIssued = false;
        session.Paused = false; session.PausePositionKnown = false; session.Faulted = false; session.FaultCode = 0; session.Error = string.Empty;
        session.FaultStopAttempted = false;
        session.LastVelocity = double.NaN; session.LastAcceleration = double.NaN;
        RestartCoordinateDeadline(session);
        PublishCoordinate(session);
        EnsureCoordinatePump(session);
        return true;
    }

    protected override bool SubmitLinearPathCore(CoordinateId coordinate, List<Position3D> points, CoordinateConfiguration config)
        => SubmitTrajectoryCore(coordinate, points.Select(x => new TrajectorySegment(TrajectorySegmentKind.Line, x.X, x.Y, x.Z)).ToArray(), config);

    private void EnsureCoordinatePump(CoordinateSession session)
    {
        if (session.PumpRunning) return;
        session.PumpRunning = true;
        int generation = session.Generation;
        int observedCancellation = session.CancellationSerial;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    bool finished = Synchronize(() =>
                    {
                        if (!coordinateSessions.TryGetValue(session.Type, out var current) || !ReferenceEquals(current, session) || session.Generation != generation)
                            return true;
                        observedCancellation = session.CancellationSerial;
                        return PumpCoordinateStep(session);
                    });
                    if (finished) return;
                    // 只等待托管计时，不持有SDK锁；一次原生调用的阻塞仍由厂商驱动约束。
                    await Task.Delay(Math.Max(1, session.PollInterval)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Synchronize(() =>
                {
                    if (coordinateSessions.TryGetValue(session.Type, out var current) && ReferenceEquals(current, session) && session.Generation == generation)
                        MarkCoordinateFault(session, ex, tryStop: true);
                    return 0;
                });
            }
            finally
            {
                Synchronize(() =>
                {
                    if (session.Generation == generation)
                    {
                        session.PumpRunning = false;
                        // 显式取消可能在本代泵抛异常后、finally之前到达；不能丢掉这次新的清理请求。
                        if (session.CancelRequested && session.CancellationSerial > observedCancellation)
                            EnsureCoordinatePump(session);
                    }
                    return 0;
                });
            }
        });
    }

    /// <summary>每轮最多压8段和搬一次PC缓冲，禁止在锁内等待缓冲余量或机械到位。</summary>
    private bool PumpCoordinateStep(CoordinateSession session)
    {
        if (session.Completed || session.Cleared) return true;
        if (session.CancelRequested)
        {
            if (CoordinateDeadlineExpired(session)) throw new TimeoutException("插补停止或清理超时，资源保持占用。");
            return TryCleanCoordinate(session);
        }
        if (session.Faulted) return true;
        VerifyCoordinateMapping(session);
        VerifyCoordinateAxisSafety(session);
        var raw = ReadCoordinateRaw(session);
        if (raw.Fault != 0) throw new InvalidOperationException($"插补状态故障码{raw.Fault}。");
        if (session.Paused)
        {
            if (!raw.Busy && !session.PausePositionKnown)
            {
                double[] paused = new double[3];
                Success(api.IMC_CrdGetPausePos(Handle(), CoordinateNumber(session.Type), paused), "读取插补暂停位置");
                foreach (double value in paused) Finite(value, "暂停位置");
                session.PausePosition = paused; session.PausePositionKnown = true; session.Enabled = false;
            }
            PublishCoordinate(session);
            return false;
        }
        if (CoordinateDeadlineExpired(session)) throw new TimeoutException("插补数据搬运或执行超时，需明确取消清理。");
        if (session.CompletionStopRequested)
        {
            if (!raw.Busy && raw.Fault == 0 && !session.AnyAxisBusy)
            {
                session.Enabled = false; session.Completed = true; session.Error = string.Empty;
                PublishCoordinate(session); return true;
            }
            PublishCoordinate(session); return false;
        }
        short number = CoordinateNumber(session.Type);
        int pushed = 0;
        bool sawBackpressure = false;
        while (session.Next < session.Segments.Length && pushed < 8)
        {
            int space = 0;
            Success(api.IMC_CrdGetSpace(Handle(), number, ref space), "读取插补缓冲余量");
            if (space < 0) throw new InvalidOperationException("插补缓冲余量为负数。");
            if (space == 0) { sawBackpressure = true; break; }
            var segment = session.Segments[session.Next];
            short mode = -1;
            Success(api.IMC_CrdGetIncMode(Handle(), number, ref mode), "核对运行中终点编程模式");
            if (mode != 0) throw new InvalidOperationException("终点编程模式被外部改变，停止沿用绝对轨迹。");
            bool line = segment.Kind == TrajectorySegmentKind.Line;
            SetCoordinateMotionParameters(session, line ? session.Configuration.LinearVelocity : session.Configuration.ArcVelocity,
                line ? session.Configuration.LinearAcceleration : session.Configuration.ArcAcceleration);
            uint result = EnqueueSegment(session, segment);
            if (IsBufferFull(result)) { sawBackpressure = true; break; }
            Success(result, "压入插补轨迹段");
            session.Next++; pushed++;
        }
        short finished = 0;
        uint transfer = api.IMC_CrdEndData(Handle(), number, ref finished);
        sawBackpressure |= IsBufferFull(transfer);
        if (!IsBufferFull(transfer)) Success(transfer, "搬运PC插补缓冲");
        if (transfer == ImcApi.EXE_SUCCESS && finished is not (0 or 1))
            throw new InvalidOperationException("IMC返回未知的插补搬运完成标志。");
        session.FlushComplete = session.Next == session.Segments.Length && transfer == ImcApi.EXE_SUCCESS && finished == 1;
        // 先预填充，再允许控制器运行来腾出底层缓冲；断流保护始终开启。
        if (!session.Started && session.Next > 0 &&
            (session.FlushComplete || session.Next >= Math.Min(32, session.Segments.Length) || sawBackpressure))
        {
            session.Enabled = true; // 调用发生未知异常时保守保持“可能已使能”。
            Success(api.IMC_CrdStart(Handle(), number), "启动插补持续使能");
            session.Started = true;
        }
        if (session.Started)
        {
            raw = ReadCoordinateRaw(session);
            if (raw.Fault != 0) throw new InvalidOperationException($"插补状态故障码{raw.Fault}。");
            double[] positions = ReadCoordinateAxes(session);
            session.ObservedProgress |= raw.Busy || positions.Where((p, index) => Math.Abs(p - session.InitialPosition[index]) > session.Configuration.ArrivalTolerance).Any();
            short arrived = 0;
            Success(api.IMC_CrdGetArrivalSts(Handle(), number, ref arrived), "读取最后一段到位反馈");
            bool target = positions.Where((p, index) => Math.Abs(p - session.FinalPosition[index]) > session.Configuration.ArrivalTolerance).Any() == false;
            if (session.FlushComplete && !raw.Busy && !session.AnyAxisBusy && arrived == 1 && target && (session.ObservedProgress || session.NoOp))
            {
                // 即使最后一段已到位，Start仍是持续使能。明确停止并读回静止后才发布完成。
                session.CompletionStopRequested = true;
                Success(api.IMC_CrdStop(Handle(), number, 0), "结束已到位插补的持续使能");
                var stopped = ReadCoordinateRaw(session);
                if (!stopped.Busy && stopped.Fault == 0 && !AnyCoordinateAxisBusy(session))
                {
                    session.Enabled = false; session.Completed = true; session.Error = string.Empty;
                    PublishCoordinate(session); return true;
                }
            }
        }
        PublishCoordinate(session);
        return false;
    }

    private uint EnqueueSegment(CoordinateSession session, TrajectorySegment segment)
    {
        short number = CoordinateNumber(session.Type);
        short direction = segment.Clockwise ? (short)-1 : (short)1; // C02：1逆时针，-1顺时针。
        double[] center = [segment.CenterU, segment.CenterV];
        return segment.Kind switch
        {
            TrajectorySegmentKind.Line => api.IMC_CrdLineXYZ(Handle(), number, [segment.X, segment.Y, segment.Z], segment.UserId),
            TrajectorySegmentKind.ArcXY => api.IMC_CrdArcCenterXYPlane(Handle(), number, center, [segment.X, segment.Y], direction, 0, 0, segment.UserId),
            TrajectorySegmentKind.ArcYZ => api.IMC_CrdArcCenterYZPlane(Handle(), number, center, [segment.Y, segment.Z], direction, 0, 0, segment.UserId),
            TrajectorySegmentKind.ArcZX => api.IMC_CrdArcCenterZXPlane(Handle(), number, center, [segment.Z, segment.X], direction, 0, 0, segment.UserId),
            _ => throw new ArgumentOutOfRangeException(nameof(segment), "未实现的插补段类型。")
        };
    }
    private static bool IsBufferFull(uint code) => code is ImcApi.ERR_CRD_BUF_FULL or ImcApi.ERR_FIFO_FULL;

    /// <summary>超时采用单调时钟；修改Windows时间不会提前终止或无限延长任务。</summary>
    private void RestartCoordinateDeadline(CoordinateSession session)
    {
        session.DeadlineStarted = clock.GetTimestamp();
        session.DeadlineBudget = TimeSpan.FromMilliseconds(session.Timeout);
    }
    private bool CoordinateDeadlineExpired(CoordinateSession session)
        => clock.GetElapsedTime(session.DeadlineStarted) >= session.DeadlineBudget;

    private (bool Busy, int Fault) ReadCoordinateRaw(CoordinateSession session)
    {
        short value = 0;
        Success(api.IMC_CrdGetStatus(Handle(), CoordinateNumber(session.Type), ref value), "读取插补运行和故障状态");
        ushort word = unchecked((ushort)value);
        int running = word & 0xff;
        if (running is not (0 or 1)) throw new InvalidOperationException("IMC返回未知插补运行状态，未按空闲处理。");
        session.Raw = word;
        return (running == 1, word >> 8);
    }
    private double[] ReadCoordinateAxes(CoordinateSession session)
    {
        var result = new double[3];
        for (int index = 0; index < 3; index++)
        {
            ValidateCurrentBinding(Handle(), session.Axes[index]);
            double[] position = new double[1];
            Success(api.IMC_GetAxEncPos(Handle(), session.Mapping[index], position), "核对插补轴实际位置");
            Finite(position[0], "插补轴反馈"); result[index] = position[0];
        }
        return result;
    }

    private void VerifyCoordinateAxisSafety(CoordinateSession session)
    {
        // 任务是异步压入的，启动前检查不能替代后续每轮反馈。Busy可属于本任务，不因此拒绝。
        session.AnyAxisBusy = false;
        foreach (short axis in session.Mapping)
        {
            int[] sample = new int[1];
            Success(api.IMC_GetAxSts(Handle(), axis, sample), "读取插补轴运行保护状态");
            uint status = unchecked((uint)sample[0]);
            session.AnyAxisBusy |= (status & ImcApi.AX_BUSY_BIT) != 0;
            if ((status & ImcApi.AX_SVON_BIT) == 0 ||
                (status & (ImcApi.AX_ALARM_BIT | ImcApi.AX_ERRPOS_BIT | ImcApi.AX_EMG_STOP_BIT | ImcApi.AX_SW_ABNOR_BIT)) != 0)
                throw new InvalidOperationException("插补期间轴失去使能、报警或急停，停止继续压入新轨迹。");
        }
    }

    private bool AnyCoordinateAxisBusy(CoordinateSession session)
    {
        bool busy = false;
        foreach (short axis in session.Mapping)
        {
            int[] sample = new int[1];
            Success(api.IMC_GetAxSts(Handle(), axis, sample), "确认插补关联轴已停稳");
            busy |= (unchecked((uint)sample[0]) & ImcApi.AX_BUSY_BIT) != 0;
        }
        return busy;
    }

    protected override bool ReadCoordinateStatesCore(CoordinateId? coordinate)
    {
        if (coordinate.HasValue) CoordinateNumber(coordinate.Value);
        foreach (var state in CoordinateStateBuffer.Where(x => !coordinate.HasValue || x.CoordinateId == coordinate.Value))
        {
            try
            {
                if (coordinateSessions.TryGetValue(state.CoordinateId, out var session))
                {
                    if (session.Owned) VerifyCoordinateMapping(session);
                    var raw = session.Owned ? ReadCoordinateRaw(session) : (Busy: false, Fault: 0);
                    if (raw.Fault != 0 && !session.CancelRequested)
                    {
                        session.FaultCode = raw.Fault;
                        MarkCoordinateFault(session, new InvalidOperationException($"IMC插补故障码{raw.Fault}，需明确停止和清理。"), tryStop: true);
                    }
                    PublishCoordinate(session);
                }
                else
                {
                    short[] axes = new short[3]; short ahead = 0; double deceleration = 0;
                    uint result = api.IMC_CrdGetMtSysParam(Handle(), CoordinateNumber(state.CoordinateId), axes, ref ahead, ref deceleration);
                    if (result != ImcApi.EXE_SUCCESS && result != ImcApi.ERR_CRD_NOT_EXIST) Success(result, "读取坐标系是否存在");
                    state.Exist = result == ImcApi.EXE_SUCCESS; state.Valid = true; state.UpdatedAt = CurrentTime;
                    state.Busy = false; state.Paused = false; state.Arrived = false; state.Enabled = state.Exist; state.Queued = state.Exist;
                    state.Cleared = false; state.Alarm = false; state.FaultCode = 0;
                    if (state.Exist)
                    {
                        short raw = 0;
                        Success(api.IMC_CrdGetStatus(Handle(), CoordinateNumber(state.CoordinateId), ref raw), "只读外部坐标系状态");
                        ushort word = unchecked((ushort)raw);
                        if ((word & 0xff) is not (0 or 1)) throw new InvalidOperationException("外部坐标系状态未知。");
                        state.RawStatus = word; state.Busy = (word & 0xff) == 1;
                        state.FaultCode = word >> 8; state.Alarm = state.FaultCode != 0;
                    }
                    state.ErrorMessage = state.Exist ? "外部坐标系，仅识别存在；本驱动未接管。" : string.Empty;
                }
            }
            catch (Exception ex) { state.Valid = false; state.ErrorMessage = ex.Message; throw; }
        }
        return true;
    }

    private void PublishCoordinate(CoordinateSession session)
    {
        var state = CoordinateState(session.Type);
        state.Exist = session.Owned; state.Valid = true; state.UpdatedAt = CurrentTime; state.RawStatus = session.Raw;
        state.Paused = session.Paused;
        state.Enabled = session.Enabled;
        state.Queued = session.HasJob && !session.Completed && !session.Cleared || session.Faulted;
        state.Busy = (session.Raw & 0xff) == 1 || session.HasJob && !session.Completed && !session.Cleared && !session.Paused && !session.Faulted && !session.CancelRequested;
        state.Alarm = session.Faulted || (session.Raw >> 8) != 0;
        state.Arrived = session.Completed;
        state.Cleared = session.Cleared;
        state.FaultCode = session.FaultCode != 0 ? session.FaultCode : session.Raw >> 8;
        state.ErrorMessage = session.Error;
    }

    private void MarkCoordinateFault(CoordinateSession session, Exception exception, bool tryStop)
    {
        session.Faulted = true; session.Completed = false; session.Cleared = false;
        session.Error = exception.Message;
        if (tryStop && session.Owned && !session.FaultStopAttempted)
        {
            session.FaultStopAttempted = true;
            try
            {
                if (VerifyCoordinateOwnership(session, allowMissing: true))
                    Success(api.IMC_CrdStop(Handle(), CoordinateNumber(session.Type), 0), "异常后请求插补停止");
            }
            catch (Exception stopError) { session.Error += "；停止未确认：" + stopError.Message; }
        }
        PublishCoordinate(session);
    }

    protected override bool PauseCoordinateCore(CoordinateId coordinate)
    {
        var session = OwnedCoordinate(coordinate);
        if (!session.HasJob || session.Completed || session.Cleared || session.CancelRequested || session.Faulted)
            throw new InvalidOperationException("没有可以暂停的本会话轨迹。");
        VerifyCoordinateMapping(session);
        session.Paused = true; session.PauseStarted = clock.GetTimestamp(); session.PausePositionKnown = false;
        try
        {
            // 先停止继续压入；原生停止发生不确定异常时不能让后台泵继续发轨迹。
            Success(api.IMC_CrdStop(Handle(), CoordinateNumber(coordinate), 0), "请求插补暂停");
            PublishCoordinate(session); EnsureCoordinatePump(session); return true;
        }
        catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: true); throw; }
    }

    protected override bool ResumeCoordinateCore(CoordinateId coordinate)
    {
        var session = OwnedCoordinate(coordinate);
        if (!session.Paused || !session.PausePositionKnown || session.CancelRequested || session.Faulted)
            throw new InvalidOperationException("暂停尚未停稳、暂停位置未确认或任务已经撤销。");
        VerifyCoordinateMapping(session);
        var raw = ReadCoordinateRaw(session);
        if (raw.Busy || raw.Fault != 0) throw new InvalidOperationException("插补轴仍在停止或有故障。");
        var actual = ReadCoordinateAxes(session);
        if (actual.Where((position, index) => Math.Abs(position - session.PausePosition[index]) > session.Configuration.ArrivalTolerance).Any())
            throw new InvalidOperationException("暂停期间插补轴位置已改变，拒绝从错误位置继续旧轨迹。");
        short mode = -1;
        Success(api.IMC_CrdGetIncMode(Handle(), CoordinateNumber(coordinate), ref mode), "核对恢复时终点编程方式");
        if (mode != 0) throw new InvalidOperationException("暂停期间终点编程方式已改变，拒绝继续旧轨迹。");
        session.DeadlineBudget += clock.GetElapsedTime(session.PauseStarted);
        try
        {
            if (session.Started)
            {
                session.Enabled = true;
                Success(api.IMC_CrdStart(Handle(), CoordinateNumber(coordinate)), "恢复插补持续使能");
            }
            session.Paused = false; session.PausePositionKnown = false;
            PublishCoordinate(session); EnsureCoordinatePump(session); return true;
        }
        catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: true); throw; }
    }

    protected override bool CancelTrajectoryCore(CoordinateId coordinate)
    {
        var session = OwnedCoordinate(coordinate);
        try { RequestCoordinateCancel(session, StopMode.Decelerate); EnsureCoordinatePump(session); return true; }
        catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: false); throw; }
    }

    private void RequestCoordinateCancel(CoordinateSession session, StopMode mode)
    {
        session.CancelRequested = true; session.Paused = false; session.Completed = false; session.Cleared = false;
        session.CancellationSerial++;
        session.StopMode = mode; RestartCoordinateDeadline(session);
        session.StopIssued = false;
        if (!VerifyCoordinateOwnership(session, allowMissing: true))
        {
            session.Owned = false; session.Prepared = false; session.Raw = 0;
            FinishCoordinateCleanup(session); return;
        }
        // 不把API接受停止当机械已经静止；后续步进必须读回静止并完成清理。
        uint result = api.IMC_CrdStop(Handle(), CoordinateNumber(session.Type), mode == StopMode.Decelerate ? (short)0 : (short)1);
        if (result != ImcApi.ERR_CRD_NOT_EXIST) Success(result, "请求取消插补并停止");
        session.StopIssued = true;
        PublishCoordinate(session);
    }

    private bool TryCleanCoordinate(CoordinateSession session)
    {
        short number = CoordinateNumber(session.Type);
        if (!session.Owned || !VerifyCoordinateOwnership(session, allowMissing: true))
        {
            // 创建发生不确定失败后，明确读到不存在可结束清理责任，不伪造到位。
            session.Owned = false; session.Prepared = false; session.Raw = 0;
            FinishCoordinateCleanup(session); return true;
        }
        if (!session.StopIssued)
        {
            Success(api.IMC_CrdStop(Handle(), number, session.StopMode == StopMode.Decelerate ? (short)0 : (short)1), "重试插补停止");
            session.StopIssued = true;
        }
        var stopped = ReadCoordinateRaw(session);
        if (stopped.Busy || AnyCoordinateAxisBusy(session)) { PublishCoordinate(session); return false; }
        Success(api.IMC_CrdClrData(Handle(), number), "清除本任务插补队列");
        Success(api.IMC_CrdClrError(Handle(), number), "清除已停止插补错误");
        stopped = ReadCoordinateRaw(session);
        if (stopped.Busy || stopped.Fault != 0 || AnyCoordinateAxisBusy(session)) throw new InvalidOperationException("插补清理后仍有运行或故障状态。");
        FinishCoordinateCleanup(session); return true;
    }

    private void FinishCoordinateCleanup(CoordinateSession session)
    {
        session.Enabled = false; session.HasJob = false; session.Paused = false;
        session.Faulted = false; session.FaultCode = 0; session.Error = string.Empty; session.Cleared = true;
        session.FaultStopAttempted = false;
        session.CancelRequested = false; session.Completed = false; session.Segments = [];
        PublishCoordinate(session);
    }

    protected override bool ClearCoordinateCore(CoordinateId coordinate)
    {
        var session = OwnedCoordinate(coordinate);
        try
        {
            RequestCoordinateCancel(session, StopMode.Decelerate);
            bool completed = TryCleanCoordinate(session);
            if (!completed) EnsureCoordinatePump(session);
            // 基类据返回值释放轴，因此只有此刻已经核对全部清理成功才返回true。
            return completed;
        }
        catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: false); throw; }
    }

    /// <summary>单轴Stop可能涉及整个插补组，所有关联任务都进入撤销；失败也不阻断其他坐标系停止。</summary>
    private void StopCoordinates(AxisId? axis, StopMode mode)
    {
        List<string> failures = [];
        foreach (var session in coordinateSessions.Values.Where(x => !axis.HasValue || x.Axes.Contains(axis.Value)).ToArray())
        {
            try { RequestCoordinateCancel(session, mode); EnsureCoordinatePump(session); }
            catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: false); failures.Add(ex.Message); }
        }
        if (failures.Count != 0) throw new InvalidOperationException("关联插补停止未全部确认：" + string.Join("；", failures));
    }

    /// <summary>关闭不等待后台线程；在串行锁内做一次有界清理，未停稳则保留句柄供显式重试。</summary>
    private void CloseCoordinates()
    {
        List<string> failures = [];
        foreach (var session in coordinateSessions.Values.ToArray())
        {
            try
            {
                // Close可能随后使Config失效，先终止本代后台泵，失败时仅保留冻结清理上下文。
                session.Generation++; session.PumpRunning = false;
                if (!session.Owned) { coordinateSessions.Remove(session.Type); continue; }
                RequestCoordinateCancel(session, StopMode.Decelerate);
                if (!TryCleanCoordinate(session)) throw new InvalidOperationException("插补轴尚未停稳，关闭需稍后重试。");
                if (session.Owned)
                {
                    uint result = api.IMC_CrdDeleteMtSys(Handle(), CoordinateNumber(session.Type));
                    if (result != ImcApi.ERR_CRD_NOT_EXIST) Success(result, "删除本会话坐标系");
                }
                session.Generation++; session.Owned = false; coordinateSessions.Remove(session.Type);
            }
            catch (Exception ex) { MarkCoordinateFault(session, ex, tryStop: false); failures.Add(ex.Message); }
        }
        if (failures.Count != 0) throw new InvalidOperationException("坐标系资源仍未释放：" + string.Join("；", failures));
    }

    /// <summary>轻量更新已登记任务，不递归读取单轴，也不在轴读取期间执行新运动。</summary>
    private void ObserveCoordinateAxes()
    {
        foreach (var session in coordinateSessions.Values) PublishCoordinate(session);
    }
}

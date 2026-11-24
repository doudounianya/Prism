using System.Diagnostics;

namespace Company.Hardware.ControlCard;

/// <summary>坐标系状态、XYZ 路径和轨迹任务；集中维护暂停恢复、路径校验及参与轴占用。</summary>
public abstract partial class MotionControllerBase
{
    #region 坐标系状态与直线路径

    private CoordinateState RefreshCoordinate(CoordinateId type)
    {
        Require(type.IsValid && CoordinateIds.Contains(type), "坐标系编号无效。");
        var state = CoordinateStateBuffer.Single(x => x.CoordinateId == type);
        try
        {
            Require(ReadCoordinateStatesCore(type) && state.Valid, "坐标系状态读取失败或无效。");
            ObserveCoordinateClaim(state);
            return state;
        }
        catch { state.Valid = false; throw; }
    }

    public bool RefreshCoordinateState(CoordinateId? coordinateId) => Execute("读取坐标系状态", () =>
    {
        if (coordinateId.HasValue) { RefreshCoordinate(coordinateId.Value); return true; }
        try
        {
            bool result = ReadCoordinateStatesCore(null) && CoordinateStateBuffer.All(x => x.Valid);
            if (!result) foreach (var state in CoordinateStateBuffer) state.Valid = false;
            else foreach (var state in CoordinateStateBuffer) ObserveCoordinateClaim(state);
            return result;
        }
        catch { foreach (var state in CoordinateStateBuffer) state.Valid = false; throw; }
    }, out _);

    /// <summary>XYZ 路径是全直线轨迹的便捷入口，统一使用轨迹任务的占用、参数快照和异常处理。</summary>
    public bool MoveLinearPath(CoordinateId coordinateId, List<Position3D> points, out string message) => Execute("XYZ直线轨迹启动", () =>
    {
        Require(points is not null && points.Count is > 0 and <= 10000 && points.All(x => x is not null), "轨迹点列表为空或超出数量上限。");
        return StartTrajectoryCore(coordinateId, points.Select(x => new TrajectorySegment(TrajectorySegmentKind.Line, x.X, x.Y, x.Z)).ToArray());
    }, out message);

    public bool PauseCoordinate(CoordinateId coordinateId, out string message) => Execute("坐标系暂停", () =>
    {
        var state = RefreshCoordinate(coordinateId);
        Require(state.Exist && state.Busy, "坐标系不存在或未运行。");
        bool result = PauseCoordinateCore(coordinateId);
        if (result)
        {
            state.Paused = true;
            var config = Configuration.Coordinates.Single(x => x.CoordinateId == coordinateId);
            foreach (var axis in new[] { config.XAxis, config.YAxis, config.ZAxis })
            {
                State(axis).Paused = true;
                if (_pending.Remove(axis, out var motion)) _pausedMotion[axis] = motion;
            }
        }
        return result;
    }, out message);

    public bool ResumeCoordinate(CoordinateId coordinateId, out string message) => Execute("坐标系恢复", () =>
    {
        var state = RefreshCoordinate(coordinateId);
        Require(state.Exist && state.Paused && !state.Busy && !state.Alarm, "坐标系未暂停、仍运动或报警。");
        var config = Configuration.Coordinates.Single(x => x.CoordinateId == coordinateId);
        foreach (var axis in new[] { config.XAxis, config.YAxis, config.ZAxis }) MotionAxis(axis, allowPaused: true, allowedOwner: coordinateId);
        bool result = ResumeCoordinateCore(coordinateId);
        if (result)
        {
            state.Paused = false;
            foreach (var axis in new[] { config.XAxis, config.YAxis, config.ZAxis })
            {
                State(axis).Paused = false;
                if (_pausedMotion.Remove(axis, out var motion)) MarkPending(axis, motion.Target, motion.Home, motion.Coordinate);
                else MarkPending(axis, coordinate: coordinateId);
            }
        }
        return result;
    }, out message);

    public bool ClearCoordinate(CoordinateId coordinateId, out string message) => Execute("清空坐标系缓存", () =>
    {
        var state = RefreshCoordinate(coordinateId);
        Require(state.Exist && !state.Busy, "坐标系不存在或仍在运动。");
        bool result = ClearCoordinateCore(coordinateId);
        if (result)
        {
            state.Paused = false;
            if (_coordinateClaims.TryGetValue(coordinateId, out var claim)) ReleaseCoordinateClaim(claim);
            var config = Configuration.Coordinates.Single(x => x.CoordinateId == coordinateId);
            foreach (var axis in new[] { config.XAxis, config.YAxis, config.ZAxis })
            {
                // 清空闲坐标系不能解除同轴上独立运动或另一个坐标系的待确认门控。
                bool ownPending = _pending.TryGetValue(axis, out var motion) && motion.Coordinate == coordinateId;
                bool ownPaused = _pausedMotion.TryGetValue(axis, out var paused) && paused.Coordinate == coordinateId;
                if (ownPending) _pending.Remove(axis);
                if (ownPaused) _pausedMotion.Remove(axis);
                if (ownPending || ownPaused) State(axis).Paused = false;
            }
        }
        return result;
    }, out message);

    // 厂商只覆盖本功能所需的扩展点；公共校验仍通过上方入口统一执行。
    protected abstract bool ReadCoordinateStatesCore(CoordinateId? coordinateId);
    protected abstract bool SubmitLinearPathCore(CoordinateId coordinateId, List<Position3D> pos, CoordinateConfiguration coordinateConfiguration);
    protected abstract bool PauseCoordinateCore(CoordinateId coordinateId);
    protected abstract bool ResumeCoordinateCore(CoordinateId coordinateId);
    protected abstract bool ClearCoordinateCore(CoordinateId coordinateId);

    #endregion

    #region 轨迹任务、路径校验与轴占用

    private sealed class CoordinateClaim(CoordinateId coordinate, AxisId[] axes)
    {
        internal readonly CoordinateId Coordinate = coordinate;
        internal readonly AxisId[] Axes = axes;
        internal bool Submitted;
        internal bool CleanupRequested;
    }
    private readonly Dictionary<CoordinateId, CoordinateClaim> _coordinateClaims = [];
    // 保留最近任务版本，即使两个任务在一次等待轮询间隙内先后结束，也不能把新任务算成旧任务完成。
    private readonly Dictionary<CoordinateId, long> _coordinateVersions = [];
    private long _coordinateVersionSequence;

    private CoordinateId? CoordinateOwner(AxisId axis)
        => _coordinateClaims.Values.FirstOrDefault(x => x.Axes.Contains(axis))?.Coordinate;

    /// <summary>
    /// 轴Busy可能在数据搬运、暂停或段间暂时为false；轴的占用必须跟随整个轨迹任务。
    /// 只在本会话同步读取成功之后接受“自然完成并解除持续使能”或“明确清理成功”反馈。
    /// 提交/清理状态及对象身份隔离前后任务，不用可能被系统校时回拨的UTC排序证明新鲜度。
    /// </summary>
    private void ObserveCoordinateClaim(CoordinateState state)
    {
        if (!_coordinateClaims.TryGetValue(state.CoordinateId, out var claim) || !state.Valid) return;
        bool finished = !state.Busy && !state.Paused && !state.Alarm && !state.Enabled && !state.Queued &&
            (claim.Submitted && state.Arrived || (claim.Submitted || claim.CleanupRequested) && state.Cleared);
        if (finished) ReleaseCoordinateClaim(claim);
    }

    private void ReleaseCoordinateClaim(CoordinateClaim claim)
    {
        if (!_coordinateClaims.TryGetValue(claim.Coordinate, out var current) || !ReferenceEquals(current, claim)) return;
        _coordinateClaims.Remove(claim.Coordinate);
        foreach (var axis in claim.Axes)
        {
            if (_pending.TryGetValue(axis, out var motion) && motion.Coordinate == claim.Coordinate) _pending.Remove(axis);
            if (_pausedMotion.TryGetValue(axis, out var paused) && paused.Coordinate == claim.Coordinate) _pausedMotion.Remove(axis);
            State(axis).Paused = false;
        }
    }

    private AxisId[] CoordinateAxes(CoordinateConfiguration config)
    {
        AxisId[] axes = [config.XAxis, config.YAxis, config.ZAxis];
        Require(axes.All(x => x.IsValid && AxisIds.Contains(x)) && axes.Distinct().Count() == 3, "XYZ轴映射无效或重复。");
        return axes;
    }

    public bool StartTrajectory(CoordinateId coordinateId, IReadOnlyList<TrajectorySegment> segments, out string message)
        => Execute("轨迹任务启动", () => StartTrajectoryCore(coordinateId, segments), out message);

    private bool StartTrajectoryCore(CoordinateId coordinate, IReadOnlyList<TrajectorySegment> segments)
    {
        Require(coordinate.IsValid && CoordinateIds.Contains(coordinate), "坐标系编号无效。");
        Require(segments is not null && segments.Count is > 0 and <= 10000 && segments.All(x => x is not null),
            "轨迹段为空或超过10000段。");
        var copy = segments.ToArray(); // 不持有调用者可继续修改的列表。
        var config = Configuration.Coordinates.SingleOrDefault(x => x.CoordinateId == coordinate)
            ?? throw new InvalidOperationException("没有对应坐标系配置。");
        Require(config.CoordinateEnabled, "此坐标系未启用。");
        if (_coordinateClaims.ContainsKey(coordinate)) RefreshCoordinate(coordinate);
        Require(!_coordinateClaims.ContainsKey(coordinate), "此坐标系仍有未完成或未清理的任务。");
        var axes = CoordinateAxes(config);
        var states = axes.Select(x => MotionAxis(x)).ToArray();
        ValidateTrajectory(copy, axes, states, config);
        var claimedAxes = axes.Concat(Configuration.Synchronizations.Where(s => s.Enabled && axes.Contains(s.MasterAxis)).Select(s => s.SlaveAxis)).Distinct().ToArray();
        var claim = new CoordinateClaim(coordinate, claimedAxes);
        _coordinateClaims.Add(coordinate, claim);
        _coordinateVersions[coordinate] = ++_coordinateVersionSequence;
        var last = copy[^1]; double[] final = [last.X, last.Y, last.Z];
        var pending = axes.Select((axis, i) => (axis, MarkPending(axis, final[i], coordinate: coordinate))).ToArray();
        try
        {
            // 先占用轴再创建坐标系。原生调用抛异常不能证明未提交，保留占用待明确清理。
            if (!DoPrepareCoordinate(coordinate, config)) { ReleaseCoordinateClaim(claim); return false; }
            var state = RefreshCoordinate(coordinate);
            Require(state.Exist && !state.Busy && !state.Paused && !state.Alarm && !state.Enabled && !state.Queued,
                "坐标系未准备好或仍持有运行任务。");
            bool accepted = SubmitPending(() => SubmitTrajectoryCore(coordinate, copy, config), pending);
            if (!accepted) { ReleaseCoordinateClaim(claim); return false; }
            claim.Submitted = true;
            state.Arrived = false; state.Cleared = false; state.Paused = false;
            foreach (var axis in states) axis.Arrived = false;
            return true;
        }
        catch (NotSupportedException) { ReleaseCoordinateClaim(claim); throw; }
    }

    public bool CancelTrajectory(CoordinateId coordinateId, out string message) => Execute("取消轨迹任务", () =>
    {
        Require(coordinateId.IsValid && CoordinateIds.Contains(coordinateId), "坐标系编号无效。");
        if (_coordinateClaims.TryGetValue(coordinateId, out var claim)) claim.CleanupRequested = true;
        // 驱动应只撤销自己登记的坐标系，绝不清理其他程序的控制器队列。
        bool accepted = CancelTrajectoryCore(coordinateId);
        if (accepted) RefreshCoordinate(coordinateId);
        return accepted;
    }, out message);

    /// <summary>每轮只占用一次读取的锁；等待期间Stop/Close/Cancel仍可进入。</summary>
    public bool WaitForTrajectory(CoordinateId coordinateId, TimeSpan timeout, CancellationToken cancellationToken, out string message)
    {
        message = "轨迹等待失败。";
        if (!(coordinateId.IsValid && CoordinateIds.Contains(coordinateId)) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        { message = "坐标系无效或等待时间不在0至5分钟范围内。"; return false; }
        var watch = Stopwatch.StartNew(); long? session = null;
        CoordinateClaim? waitingFor = null; bool capturedClaim = false;
        long waitingVersion = 0;
        while (watch.Elapsed < timeout)
        {
            if (cancellationToken.IsCancellationRequested) { message = "已取消等待，轨迹停止须单独请求。"; return false; }
            int poll = 20; bool entered = false;
            try
            {
                var remaining = timeout - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                if (!Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(Math.Min(20, remaining.TotalMilliseconds)))) continue;
                entered = true;
                Require(_initialized && (!session.HasValue || session == _session), "控制卡会话已变化。");
                session ??= _session; poll = Configuration.PollIntervalMs;
                var currentClaim = _coordinateClaims.GetValueOrDefault(coordinateId);
                if (!capturedClaim)
                { waitingFor = currentClaim; waitingVersion = _coordinateVersions.GetValueOrDefault(coordinateId); capturedClaim = true; }
                else if (_coordinateVersions.GetValueOrDefault(coordinateId) != waitingVersion ||
                    currentClaim is not null && !ReferenceEquals(currentClaim, waitingFor))
                { message = "此坐标系已替换为另一轨迹任务，旧等待已结束。"; return false; }
                var state = RefreshCoordinate(coordinateId);
                Require(!state.Alarm, string.IsNullOrWhiteSpace(state.ErrorMessage) ? "轨迹任务故障。" : state.ErrorMessage);
                if (!state.Busy && !state.Paused && !state.Enabled && !state.Queued && state.Arrived && !_coordinateClaims.ContainsKey(coordinateId))
                { message = "轨迹已经到位，并确认解除持续使能。"; return true; }
                if (state.Cleared && !_coordinateClaims.ContainsKey(coordinateId))
                { message = "轨迹已取消并清理，未到达原目标。"; return false; }
            }
            catch (Exception exception) { message = exception.Message; return false; }
            finally { if (entered) Monitor.Exit(_sync); }
            var wait = timeout - watch.Elapsed;
            if (wait <= TimeSpan.Zero) break;
            if (cancellationToken.WaitHandle.WaitOne((int)Math.Min(poll, Math.Ceiling(wait.TotalMilliseconds))))
            { message = "已取消等待，轨迹停止须单独请求。"; return false; }
        }
        message = "轨迹等待超时，任务和资源仍需读取反馈或明确取消。";
        return false;
    }

    /// <summary>兼容旧驱动：直线仍使用旧虚方法，高级圆弧没有实现时明确拒绝。</summary>
    protected virtual bool SubmitTrajectoryCore(CoordinateId coordinate, IReadOnlyList<TrajectorySegment> segments, CoordinateConfiguration config)
    {
        if (segments.Any(x => x.Kind != TrajectorySegmentKind.Line))
            throw new NotSupportedException("当前驱动尚未适配圆弧轨迹。");
        return SubmitLinearPathCore(coordinate, segments.Select(x => new Position3D { X = x.X, Y = x.Y, Z = x.Z }).ToList(), config);
    }
    protected virtual bool DoPrepareCoordinate(CoordinateId coordinate, CoordinateConfiguration config) => true;
    protected virtual bool CancelTrajectoryCore(CoordinateId coordinate) => throw new NotSupportedException("此驱动尚未适配完整轨迹取消，请使用原停止及清理接口。");

    private void ValidateTrajectory(TrajectorySegment[] segments, AxisId[] axes, AxisState[] states, CoordinateConfiguration config)
    {
        Positive(config.LinearVelocity, "轨迹速度"); Positive(config.LinearAcceleration, "轨迹加速度"); Positive(config.LinearDeceleration, "轨迹减速度");
        Require(config.LookAheadSegments is >= 0 and <= 10000, "前瞻段数不合法。");
        Positive(config.TransitionTolerance, "轨迹过渡精度");
        Positive(config.ArrivalTolerance, "到位核对容差");
        double[] start = states.Select(x => x.ActualPosition).ToArray();
        foreach (var value in start) Require(double.IsFinite(value), "轨迹起点反馈无效。");
        void CheckPosition(double[] position)
        {
            for (int i = 0; i < 3; i++) CheckTarget(states[i], AxisConfig(axes[i]), position[i]);
        }
        CheckPosition(start);
        foreach (var segment in segments)
        {
            Require(Enum.IsDefined(segment.Kind), "未知轨迹类型。");
            double[] end = [segment.X, segment.Y, segment.Z]; CheckPosition(end);
            Require(double.IsFinite(segment.CenterU) && double.IsFinite(segment.CenterV), "圆心偏移必须有限。");
            if (segment.Kind != TrajectorySegmentKind.Line)
            {
                Positive(config.ArcVelocity, "圆弧速度"); Positive(config.ArcAcceleration, "圆弧加速度");
                (int u, int v, int w) = segment.Kind switch
                { TrajectorySegmentKind.ArcXY => (0, 1, 2), TrajectorySegmentKind.ArcYZ => (1, 2, 0), _ => (2, 0, 1) };
                Require(Math.Abs(start[w] - end[w]) <= 1e-6, "当前仅支持平面圆弧，不能隐式产生螺旋运动。");
                double cu = start[u] + segment.CenterU, cv = start[v] + segment.CenterV;
                double radius = Math.Sqrt(segment.CenterU * segment.CenterU + segment.CenterV * segment.CenterV);
                Positive(radius, "圆弧半径"); Require(double.IsFinite(cu) && double.IsFinite(cv), "圆心计算溢出。");
                double ru = end[u] - cu, rv = end[v] - cv;
                double endRadius = Math.Sqrt(ru * ru + rv * rv);
                double tolerance = Math.Max(1, radius) * 1e-6;
                Require(double.IsFinite(endRadius) && Math.Abs(endRadius - radius) <= tolerance, "圆弧终点不在指定圆上。");
                Require(Math.Abs(end[u] - start[u]) + Math.Abs(end[v] - start[v]) > tolerance,
                    "起终点重合的整圆或多圈轨迹须拆成明确的圆弧段。");
                double begin = Math.Atan2(start[v] - cv, start[u] - cu), finish = Math.Atan2(rv, ru);
                static double Normalize(double angle) { double a = angle % (2 * Math.PI); return a < 0 ? a + 2 * Math.PI : a; }
                double sweep = Normalize(segment.Clockwise ? begin - finish : finish - begin);
                // 只检查终点会漏掉圆弧中途超行程。四个轴向极值覆盖本段实际经过的包络。
                for (int k = 0; k < 4; k++)
                {
                    double angle = k * Math.PI / 2;
                    double delta = Normalize(segment.Clockwise ? begin - angle : angle - begin);
                    if (delta > sweep + 1e-10) continue;
                    var position = (double[])start.Clone();
                    position[u] = cu + radius * Math.Cos(angle); position[v] = cv + radius * Math.Sin(angle);
                    CheckPosition(position);
                }
            }
            start = end;
        }
    }

    #endregion
}

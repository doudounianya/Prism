using Company.Hardware.ControlCard.None;
using Microsoft.Data.Sqlite;
using System.IO;

namespace Company.FlyScan.Runtime;

/// <summary>飞拍业务时序。宿主在一个调度线程依次提交输入和时间步，便于重放同一事件顺序。</summary>
public sealed class FlyScanWorkflow : IDisposable
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SimulatedPulseWidth = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan CompletionRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly FlyScanOutputs InactiveOutputs = new(null, null, false, null, false);
    private readonly SimulatedMachine _machine;
    private readonly object _axisOwner = new();
    private readonly string? _journalPath;
    private readonly TimeSpan _stepTimeout;
    private readonly FileStream? _journalLock;
    private readonly FlyScanHistory? _history;
    private readonly LinkedList<ScanFlightEvent> _events = new();
    private ScanJournal _journal = new(new ScanCounts(0, 0, 0), null);
    private bool _disposed;
    private long _ownershipVersion;
    private DateTimeOffset _phaseStarted;
    private DateTimeOffset? _nextCompletionRetry;
    private LinkedListNode<ScanFlightEvent>? _lastPulse;
    private RobotPoseRequest? _robotRequest;
    private TransferRequest? _transferRequest;
    private ScanMotionFeedback? _motionOrigin;

    public FlyScanWorkflow(SimulatedMachine machine, string? journalPath = null, TimeSpan? stepTimeout = null,
        FlyScanHistory? history = null)
    {
        _machine = machine;
        _history = history;
        _journalPath = journalPath;
        _stepTimeout = stepTimeout ?? DefaultStepTimeout;
        if (_stepTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stepTimeout));
        if (journalPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        _journalLock = new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Restore(journalPath); TrySaveCompletion(); }
        catch { _journalLock.Dispose(); throw; }
    }

    private void Restore(string journalPath)
    {
        if (!File.Exists(journalPath)) return;
        _journal = SimulationJson.Read<ScanJournal>(journalPath);
        if (_journal.Counts is null || _journal.Counts.Pieces < 0 || _journal.Counts.Groups < 0 || _journal.Counts.Triggers < 0)
            throw new InvalidDataException("飞拍检查点计数无效。");
        if (Work is not { } saved) return;
        var recipe = (saved.Recipe ?? throw new InvalidDataException("飞拍检查点缺少配方。")).Validate();
        if (saved.Id == Guid.Empty || saved.GroupsCompleted is < 0 or > 2 || saved.Attempt <= 0 ||
            saved.LastTrigger < 0 || !Enum.IsDefined(saved.Phase) || saved.GroupsCompleted > 0 && !saved.Loaded ||
            saved.Phase == ScanPhase.Completed && saved.GroupsCompleted != 2 ||
            saved.GroupsCompleted < 2 && saved.LastTrigger > recipe.Groups[saved.GroupsCompleted].Triggers.Count)
            throw new InvalidDataException("飞拍检查点进度无效。");
        _journal = _journal with { Workpiece = saved with { Recipe = recipe } };
        if (IsRunning)
        {
            Commit(Current with { Phase = ScanPhase.Interrupted, Reason = ScanInterruption.Restarted,
                Message = "上次运行未确认完成。核对工件和停稳状态后，重新执行未确认组。" });
            Record("中断", Current.Message);
        }
    }

    private ScanWorkpiece? Work => _journal.Workpiece;
    private ScanWorkpiece Current => Work ?? throw new InvalidOperationException("还没有飞拍工件。");
    private ScanGroup Group => Current.Recipe.Groups[Current.GroupsCompleted];
    public ScanPhase Phase => Work?.Phase ?? ScanPhase.Idle;
    public bool IsRunning => Phase is >= ScanPhase.AwaitLoad and <= ScanPhase.AwaitUnload;
    public bool CleanupPending { get; private set; }
    public bool TriggerArmed => Phase == ScanPhase.Scanning && ScanContextIsCurrent;
    public Guid? Workpiece => Work?.Id;
    public int Attempt => Work?.Attempt ?? 0;
    public int ConfirmedGroups => Work?.GroupsCompleted ?? 0;
    public int LastConfirmedTrigger => Work?.LastTrigger ?? 0;
    public ScanInterruption? Interruption => Work?.Reason;
    public FlyScanRecipe? RecipeAwaitingRecovery => Work is { Phase: ScanPhase.Interrupted } pending ? pending.Recipe : null;
    public string Message => Work?.Message ?? "请选择配方，切到自动模式后开始。";
    public ScanCounts Counts => _journal.Counts;
    public string? HistoryError { get; private set; }
    public ScanMotionFeedback? Feedback { get; private set; }
    public DateTimeOffset Clock => _machine.ScanTime;
    public IReadOnlyCollection<ScanFlightEvent> Events => _events.ToArray();
    public bool CanStart => !_disposed && (Phase is ScanPhase.Idle or ScanPhase.Completed) && _machine.CanBeginScan;
    private DateTimeOffset PulseOff => _lastPulse?.Value.Pulse?.Off ?? default;
    private bool ScanContextIsCurrent => !_disposed && _machine.OwnsScanAxes(_axisOwner) &&
        _ownershipVersion == _machine.ContextVersion;
    public FlyScanOutputs Outputs => IsRunning && ScanContextIsCurrent
        ? new(Phase == ScanPhase.AwaitLoad ? _transferRequest : null,
            _robotRequest, Phase is >= ScanPhase.AwaitReady and <= ScanPhase.ConfirmGroup,
            Phase == ScanPhase.AwaitUnload ? _transferRequest : null, Clock < PulseOff)
        : InactiveOutputs;

    public void Start(FlyScanRecipe recipe, FlyScanInputs inputs)
    {
        if (!CanStart || InputFault(inputs) is not null)
            throw new InvalidOperationException("新工件需要正常的自动模式、联机和安全反馈。");
        TrySaveCompletion();
        if (_nextCompletionRetry is not null)
            throw new InvalidOperationException("上一件历史正在等待补写。" + HistoryError);
        var snapshot = recipe.Validate();
        _machine.AcquireScanAxes(_axisOwner);
        try
        {
            Commit(new ScanWorkpiece(Guid.NewGuid(), snapshot, ScanPhase.AwaitLoad, false, 0, 1, 0, null, "等待上料确认。"));
        }
        catch
        {
            _machine.ReleaseScanAxes(_axisOwner, 0);
            throw;
        }
        _ownershipVersion = _machine.ContextVersion;
        _phaseStarted = Clock;
        Feedback = null;
        Record("工件开始", Current.Recipe.Name);
        _machine.PublishScanRunning(_axisOwner);
    }

    /// <summary>恢复须显式调用，保留已确认的组，从未完成组的起点重新执行。</summary>
    public void Recover(FlyScanInputs inputs)
    {
        if (_disposed || Phase != ScanPhase.Interrupted || InputFault(inputs) is not null)
            throw new InvalidOperationException("请清除故障和异常输入，再确认恢复。");
        if (!TryCleanup(inputs))
            throw new InvalidOperationException("尚未确认轨迹清理，请排查后重试。");
        _machine.AcquireScanAxes(_axisOwner);
        try
        {
            var attempt = Current with { Attempt = checked(Current.Attempt + 1), LastTrigger = 0, Reason = null };
            var next = !attempt.Loaded ? ScanPhase.AwaitLoad :
                attempt.GroupsCompleted == 2 ? ScanPhase.AwaitUnload : ScanPhase.AwaitRobot;
            MoveTo(next, next == ScanPhase.AwaitRobot ? "恢复组扫描，从组起点重做。" : "恢复工件握手。", attempt);
        }
        catch
        {
            _machine.ReleaseScanAxes(_axisOwner, 0);
            throw;
        }
        _ownershipVersion = _machine.ContextVersion;
        Feedback = null;
        Record("恢复", $"保留 {ConfirmedGroups} 个已确认组。");
        _machine.PublishScanRunning(_axisOwner);
    }

    public void Interrupt(ScanInterruption reason, FlyScanInputs inputs)
    {
        if (_disposed || !IsRunning) return;
        var previous = Phase;
        _robotRequest = null;
        _transferRequest = null;
        string description = Describe(reason);
        var text = previous is ScanPhase.Scanning or ScanPhase.ConfirmGroup
            ? $"{description}。第 {ConfirmedGroups + 1} 组未确认完成。已确认脉冲 {LastConfirmedTrigger}/{Group.Triggers.Count}，未确认区间及组结算待核对。"
            : $"{description}。已确认组数 {ConfirmedGroups}，恢复前需核对工件和停稳反馈。";
        var stopped = Current with { Phase = ScanPhase.Interrupted, Reason = reason, Message = text };
        // 先撤防和清理，再尝试写盘。持久化故障也不能让旧路径继续运行。
        _journal = _journal with { Workpiece = stopped };
        EndTriggerPulse();
        TryCleanup(inputs, reason == ScanInterruption.Paused ? 20 : 0);
        try { Commit(stopped); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal = _journal with { Workpiece = stopped with { Reason = ScanInterruption.JournalFailed,
                Message = text + " 检查点写入失败，需修复存储后恢复。" } };
        }
        Record("中断", Message);
    }

    /// <summary>输入先于时间消费。断连、失位、Ready 撤销等不得在同一步里先产生触发。</summary>
    public void Tick(TimeSpan elapsed, FlyScanInputs inputs)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (_disposed) return;
        if (!IsRunning)
        {
            RetryPendingCompletion();
            return;
        }
        var reason = InputFault(inputs);
        if (reason is null && !ScanContextIsCurrent)
            reason = ScanInterruption.ContextChanged;
        if (reason is null && Outputs.HoldRobot && !RobotIsHolding(inputs))
            reason = ScanInterruption.RobotMoved;
        if (reason is null && Phase is >= ScanPhase.Positioning and <= ScanPhase.ConfirmGroup && !inputs.VisionReady)
            reason = ScanInterruption.ReadyLost;
        if (reason is null && elapsed >= _stepTimeout - (Clock - _phaseStarted))
            reason = ScanInterruption.Timeout;
        if (reason is { } interruption)
        {
            Interrupt(interruption, inputs);
            return;
        }
        _machine.AdvanceScanClock(elapsed);
        try { RunCurrentStep(inputs); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interrupt(ScanInterruption.JournalFailed, inputs);
        }
    }

    // A completed journal already contains the original ID and confirmation time. Retry only the SQLite transaction.
    public bool RetryPendingCompletion()
    {
        if (_disposed || _nextCompletionRetry is not { } due || _history!.Now < due) return false;
        TrySaveCompletion();
        return true;
    }

    // 业务步骤集中在这里。改变两组任务的先后、等待或恢复策略时，从此处进入。
    private void RunCurrentStep(FlyScanInputs inputs)
    {
        switch (Phase)
        {
            case ScanPhase.AwaitLoad when _transferRequest is { } load && inputs.LoadConfirmation == load:
                MoveTo(ScanPhase.AwaitRobot, "上料确认，等待机器人到示教位。", Current with { Loaded = true });
                break;
            case ScanPhase.AwaitRobot when RobotIsHolding(inputs):
                MoveTo(ScanPhase.AwaitReady, "机器人到位保持，等待视觉 Ready。");
                break;
            case ScanPhase.AwaitReady when inputs.VisionReady:
                StartPositioning(inputs);
                break;
            case ScanPhase.Positioning:
            case ScanPhase.Scanning:
                PollMotion(inputs);
                break;
            case ScanPhase.ConfirmGroup when Clock >= PulseOff:
                ConfirmGroup();
                break;
            case ScanPhase.AwaitUnload when _transferRequest is { } unload && inputs.UnloadConfirmation == unload:
                Commit(Current with { Phase = ScanPhase.Completed, Message = "两组扫描和下料已确认。",
                        CompletedAt = _history?.Now ?? Clock, CompletionActor = _machine.Actor },
                    Counts with { Pieces = checked(Counts.Pieces + 1) });
                _machine.ReleaseScanAxes(_axisOwner, 0);
                TrySaveCompletion();
                Record("工件完成", Message);
                break;
        }
    }

    private void StartPositioning(FlyScanInputs inputs)
    {
        var origin = _machine.ReadScanMotion(out _);
        if (origin is null)
        {
            Interrupt(ScanInterruption.MotionFailed, inputs);
            return;
        }
        _motionOrigin = origin;
        if (origin.Position.Matches(Group.Waypoints[0]))
        {
            StartScan(inputs);
            return;
        }
        if (_machine.StartScanPath([Group.Waypoints[0]], out _))
            MoveTo(ScanPhase.Positioning, "平台定位到本组起点，触发关闭。");
        else
            Interrupt(ScanInterruption.MotionFailed, inputs);
    }

    private void StartScan(FlyScanInputs inputs)
    {
        if (!_machine.StartScanPath(Group.Waypoints.Skip(1), out _))
        {
            Interrupt(ScanInterruption.MotionFailed, inputs);
            return;
        }
        MoveTo(ScanPhase.Scanning, "XYR1 连续插补，已启用仿真位置比较。");
    }

    private void PollMotion(FlyScanInputs inputs)
    {
        Feedback = _machine.ReadScanMotion(out var trace);
        if (Feedback is not { } motion || _motionOrigin is not { } origin ||
            Math.Abs((motion.R2 - origin.R2) - (motion.Position.R1 - origin.Position.R1)) > ScanPoint.Tolerance ||
            Math.Abs(motion.R2Speed - motion.R1Speed) > ScanPoint.Tolerance || !motion.Busy && !motion.Arrived)
        {
            Interrupt(ScanInterruption.MotionFailed, inputs);
            return;
        }
        if (Phase == ScanPhase.Positioning)
        {
            if (motion.Arrived && motion.Position.Matches(Group.Waypoints[0])) StartScan(inputs);
            else if (motion.Arrived) Interrupt(ScanInterruption.MotionFailed, inputs);
            return;
        }
        if (!ComparePositions(trace))
        {
            Interrupt(ScanInterruption.MotionFailed, inputs);
            return;
        }
        if (!motion.Arrived) return;
        if (LastConfirmedTrigger != Group.Triggers.Count || !motion.Position.Matches(Group.Waypoints[^1]))
            Interrupt(ScanInterruption.MotionFailed, inputs);
        else
            MoveTo(ScanPhase.ConfirmGroup, "最后一点已到达，等待本组确认。");
    }

    private bool ComparePositions(IReadOnlyList<SimulationPathSlice> trace)
    {
        foreach (var slice in trace)
        {
            var start = new ScanPoint(slice.Start.X, slice.Start.Y, slice.Start.Z);
            var end = new ScanPoint(slice.End.X, slice.End.Y, slice.End.Z);
            while (LastConfirmedTrigger < Group.Triggers.Count)
            {
                var target = Group.Triggers[LastConfirmedTrigger];
                if (target.Segment < slice.Segment) return false;
                if (target.Segment != slice.Segment || !start.TryCross(end, target.Position, out double fraction)) break;
                var on = slice.StartedAt + TimeSpan.FromTicks((long)Math.Round(slice.Duration.Ticks * fraction));
                if (on <= PulseOff) return false; // 两个上升沿之间须有低电平区间。
                var at = new ScanPoint(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction,
                    start.R1 + (end.R1 - start.R1) * fraction);
                var pulse = new TriggerPulse(target.Name, at, on, on + SimulatedPulseWidth);
                Commit(Current with { LastTrigger = LastConfirmedTrigger + 1 },
                    Counts with { Triggers = checked(Counts.Triggers + 1) });
                Record("触发", target.Name, pulse);
            }
        }
        return true;
    }

    private void EndTriggerPulse()
    {
        var stoppedAt = Clock;
        if (_lastPulse is { Value.Pulse: { } pulse } active && stoppedAt < pulse.Off)
            active.Value = active.Value with { Pulse = pulse with { Off = stoppedAt } };
        _lastPulse = null;
    }

    private void ConfirmGroup()
    {
        string name = Group.Name;
        int completed = ConfirmedGroups + 1;
        var next = completed == Current.Recipe.Groups.Count ? ScanPhase.AwaitUnload : ScanPhase.AwaitRobot;
        MoveTo(next, next == ScanPhase.AwaitUnload ? "两组扫描已确认，等待下料确认。" : "等待机器人切换到下一示教位。",
            Current with { GroupsCompleted = completed, LastTrigger = 0 },
            Counts with { Groups = checked(Counts.Groups + 1) });
        Record("组完成", name, group: completed);
    }

    private bool RobotIsHolding(FlyScanInputs inputs) => _robotRequest is { } request &&
        inputs.Robot is { InPosition: true, Moving: false } feedback &&
        feedback.Command == request.Command && feedback.Pose == request.Pose;

    private static ScanInterruption? InputFault(FlyScanInputs inputs) => inputs switch
    {
        { EmergencyStop: true } => ScanInterruption.Emergency,
        { Alarm: true } => ScanInterruption.Alarm,
        { CardConnected: false } => ScanInterruption.Disconnected,
        _ => null
    };

    private static string Describe(ScanInterruption reason) => reason switch
    {
        ScanInterruption.Cancelled => "已取消本次尝试",
        ScanInterruption.Paused => "已请求暂停",
        ScanInterruption.Stopped => "已请求停止",
        ScanInterruption.ReadyLost => "视觉 Ready 丢失",
        ScanInterruption.RobotMoved => "机器人失去到位保持",
        ScanInterruption.Disconnected => "运动卡断连",
        ScanInterruption.Timeout => "等待反馈超时",
        ScanInterruption.Alarm => "仿真告警",
        ScanInterruption.Emergency => "仿真急停",
        ScanInterruption.ContextChanged => "模式、状态或角色已变化",
        ScanInterruption.MotionFailed => "运动反馈、同步或位置比较异常",
        ScanInterruption.JournalFailed => "检查点保存失败",
        _ => "仿真进程已重启"
    };

    private bool TryCleanup(FlyScanInputs inputs, int stoppedStatus = 0)
    {
        if (!_machine.OwnsScanAxes(_axisOwner))
            return !CleanupPending && inputs.CardConnected && !inputs.BlockCleanup;
        Feedback = inputs.CardConnected && !inputs.BlockCleanup && _machine.ClearScanPath()
            ? _machine.ReadScanMotion(out _) : null;
        CleanupPending = Feedback is not { Busy: false, R1Speed: 0, R2Speed: 0 };
        if (!CleanupPending) _machine.ReleaseScanAxes(_axisOwner, stoppedStatus);
        return !CleanupPending;
    }

    private void MoveTo(ScanPhase phase, string message, ScanWorkpiece? changed = null, ScanCounts? counts = null)
    {
        Commit((changed ?? Current) with { Phase = phase, Message = message }, counts);
        _phaseStarted = Clock;
        if (phase == ScanPhase.AwaitRobot) _robotRequest = new(Guid.NewGuid(), Group.RobotPose);
        if (phase is ScanPhase.AwaitLoad or ScanPhase.AwaitUnload) _robotRequest = null;
        Record("步骤", message);
    }

    private void Commit(ScanWorkpiece workpiece, ScanCounts? counts = null)
    {
        var next = new ScanJournal(counts ?? Counts, workpiece);
        if (_journalPath is not null) SimulationJson.Write(_journalPath, next);
        // 握手号随已确认的步骤生成。恢复保留工件 ID，但旧请求的确认不能推进新尝试。
        if (Work?.Id != workpiece.Id || Phase != workpiece.Phase)
            _transferRequest = workpiece.Phase is ScanPhase.AwaitLoad or ScanPhase.AwaitUnload
                ? new TransferRequest(Guid.NewGuid(), workpiece.Id) : null;
        _journal = next;
    }

    private void Record(string kind, string description, TriggerPulse? pulse = null, int? group = null)
    {
        if (_events.Count == 2000) _events.RemoveFirst();
        var entry = _events.AddLast(new ScanFlightEvent(pulse?.On ?? Clock, Current.Id,
            group ?? Math.Min(Current.GroupsCompleted + 1, 2), Current.Attempt, kind, description, pulse));
        if (pulse is not null) _lastPulse = entry;
        if (_history is null || kind is not ("工件开始" or "中断" or "恢复" or "组完成")) return;
        try
        {
            _history.Record(new FlyOperation(_history.Now, _machine.Actor, Current.Id.ToString("N"), kind, description,
                kind == "中断" ? "已中断" : "已确认"));
        }
        catch (SqliteException ex) { HistoryError = ex.Message; }
    }

    private void TrySaveCompletion()
    {
        if (_history is null || Work is not { Phase: ScanPhase.Completed, CompletedAt: { } time } piece) return;
        try
        {
            _history.RecordCompletion(piece.Id, time, piece.CompletionActor ?? "飞拍仿真");
            _nextCompletionRetry = null;
            HistoryError = null;
        }
        catch (SqliteException ex)
        {
            HistoryError = ex.Message;
            _nextCompletionRetry = _history.Now + CompletionRetryInterval;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_machine.OwnsScanAxes(_axisOwner) && !CleanupPending)
            {
                EndTriggerPulse();
                TryCleanup(new());
            }
        }
        finally { _journalLock?.Dispose(); }
    }
}

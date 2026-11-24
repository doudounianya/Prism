namespace Company.FlyScan.Runtime;

public enum ScanPhase { Idle, AwaitLoad, AwaitRobot, AwaitReady, Positioning, Scanning, ConfirmGroup, AwaitUnload, Interrupted, Completed }
public enum ScanInterruption { Cancelled, Paused, Stopped, ReadyLost, RobotMoved, Disconnected, Timeout, Alarm, Emergency, ContextChanged, MotionFailed, Restarted, JournalFailed }

public sealed record RobotPoseRequest(Guid Command, string Pose);
public sealed record RobotPoseFeedback(Guid Command, string Pose, bool InPosition, bool Moving);

/// <summary>本机回路的信号快照。上下料及机器人相关标识用于仿真中隔离前后握手。</summary>
public sealed record FlyScanInputs(
    Guid? LoadedWorkpiece = null, RobotPoseFeedback? Robot = null, bool VisionReady = false,
    Guid? UnloadedWorkpiece = null, bool CardConnected = true, bool Alarm = false, bool EmergencyStop = false,
    bool BlockCleanup = false);

public sealed record FlyScanOutputs(Guid? RequestLoad, RobotPoseRequest? RequestRobot, bool HoldRobot,
    Guid? RequestUnload, bool VisionTrigger);
public sealed record TriggerPulse(string Name, ScanPoint Position, DateTimeOffset On, DateTimeOffset Off);
public sealed record ScanFlightEvent(DateTimeOffset Time, Guid Workpiece, int Group, int Attempt, string Kind,
    string Description, TriggerPulse? Pulse = null);
public sealed record ScanCounts(int Pieces, int Groups, int Triggers);
public sealed record ScanMotionFeedback(ScanPoint Position, double R2, double R1Speed, double R2Speed, bool Busy, bool Arrived);

/// <summary>仿真单工件检查点。完成数与进度在同一次原子写入提交。</summary>
internal sealed record ScanJournal(ScanCounts Counts, ScanWorkpiece? Workpiece);

internal sealed record ScanWorkpiece(Guid Id, FlyScanRecipe Recipe, ScanPhase Phase, bool Loaded,
    int GroupsCompleted, int Attempt, int LastTrigger, ScanInterruption? Reason, string Message,
    DateTimeOffset? CompletedAt = null, string? CompletionActor = null);

/// <summary>离线 IO 对端。收到新机器人请求的第一步仍在移动，之后在同一位置保持。</summary>
public sealed class SimulatedScanPeers
{
    private Guid? _robotCommand;

    public FlyScanInputs Read(FlyScanOutputs outputs, bool loaded, bool unloaded, bool robotResponds,
        bool ready, bool connected, bool alarm, bool emergency)
    {
        RobotPoseFeedback? robot = null;
        if (outputs.RequestRobot is { } request)
        {
            bool stationary = robotResponds && _robotCommand == request.Command;
            robot = new RobotPoseFeedback(request.Command, request.Pose, stationary, !stationary);
        }
        _robotCommand = outputs.RequestRobot?.Command;
        return new FlyScanInputs(loaded ? outputs.RequestLoad : null, robot, ready,
            unloaded ? outputs.RequestUnload : null, connected, alarm, emergency);
    }
}

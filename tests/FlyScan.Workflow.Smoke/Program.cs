using Company.FlyScan.Runtime;
using Company.Hardware.ControlCard;
using Company.Platform.Shared.Login;
using System.Diagnostics;
using System.Text.Json;

if (args is ["--replay-inputs", var inputDirectory])
{
    foreach (var replay in ScanInputRecording.ReplayDirectory(inputDirectory))
        Console.WriteLine($"PASS replay {replay.Scenario}: {replay.Steps} steps, {replay.Counts}");
    return;
}

var profile = Path.Combine(AppContext.BaseDirectory, "FlyScan.13Axes.json");
var recipe = FlyScanRecipe.Load(Path.Combine(AppContext.BaseDirectory, "FlyScan.DemoRecipe.json"));
if (args is ["--interrupt-process" or "--recover-process", _] or ["--interrupt-process", _, "--wait-for-termination"])
{
    string restartPath = args[1];
    using var machine = new SimulatedMachine(profile);
    machine.SelectRole(UserRole.EquipmentEngineer);
    machine.SetFeedback(20, 0);
    using var history = new FlyScanHistory(Path.ChangeExtension(restartPath, ".sqlite"));
    using var flow = new FlyScanWorkflow(machine, restartPath, history: history);
    var recording = new ScanInputRecording(recipe);
    if (args[0] == "--interrupt-process")
    {
        Require(flow.Phase == ScanPhase.Idle, "restart proof requires a new checkpoint");
        recording.Start(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Scanning && flow.ConfirmedGroups == 1 && flow.LastConfirmedTrigger == 1);
        recording.Save(restartPath + ".before-inputs.json");
        Console.WriteLine($"PROCESS_CHECKPOINT {flow.Workpiece} {flow.Counts}");
        ProcessRestartChecks.WritePhase("checkpoint", flow, history);
        Console.Out.Flush();
        if (args.Length == 3)
            Console.ReadLine();
        // Exit without disposing the simulated workflow.
        Environment.Exit(0);
    }
    else
    {
        Require(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.Restarted &&
            flow.ConfirmedGroups == 1 && flow.LastConfirmedTrigger == 1 && !flow.Outputs.VisionTrigger, "restart gates replay");
        var id = flow.Workpiece;
        var savedCounts = flow.Counts;
        recording.Restarted(flow);
        recording.Tick(flow, TimeSpan.FromMilliseconds(12), Inputs(flow));
        Require(flow.Phase == ScanPhase.Interrupted && flow.Counts == savedCounts && !flow.TriggerArmed,
            "healthy inputs alone cannot restart the scan");
        ProcessRestartChecks.WritePhase("restored", flow, history);
        recording.Recover(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
        Require(flow.Workpiece == id && flow.Counts == new ScanCounts(1, 2, 6), "restart settles same piece once");
        Require(history.Pieces(history.Now.AddDays(-1), history.Now.AddTicks(1)) is [{ Id: var savedId }] && savedId == id,
            "G4 history persists exactly one completion across processes");
        recording.Save(restartPath + ".after-inputs.json");
        ProcessRestartChecks.WritePhase("completed", flow, history);
        Console.WriteLine($"PASS PROCESS_RECOVERED {id} {flow.Counts}");
    }
    return;
}
var evidence = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "flyscan-workflow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);
string replayDirectory = Path.Combine(evidence, "input-replay");
Directory.CreateDirectory(replayDirectory);
File.Copy(profile, Path.Combine(replayDirectory, "axes.json"), overwrite: true);
var outputs = new Dictionary<string, object>();
int checks = 0;

foreach (var wrong in new[] { new ScanPoint(1.25, 1.0 / 3, 2.0 / 3),
    new ScanPoint(1, 0.5, 2.0 / 3), new ScanPoint(1, 1.0 / 3, 1) })
{
    var groups = recipe.Groups.ToArray();
    var triggers = groups[0].Triggers.ToArray();
    triggers[0] = triggers[0] with { Position = wrong };
    groups[0] = groups[0] with { Triggers = triggers };
    Reject<InvalidDataException>(() => (recipe with { Groups = groups }).Validate(),
        $"recipe rejects an inconsistent XYZ/R1 point {wrong}");
}

using (var machine = new SimulatedMachine(profile))
{
    machine.SelectRole(UserRole.Operator);
    machine.SetFeedback(0, 30);
    Reject<InvalidOperationException>(() => machine.CaptureTeachingPoint(), "operator cannot record teaching coordinates");
    machine.SelectRole(UserRole.EquipmentEngineer);
    var x = new AxisId("Front.X");
    Require(machine.TryOperateAxis(x, "Enable", out _), "enable teaching axis");
    var start1 = machine.CaptureTeachingPoint();
    var point1 = TeachX(1);
    var start2 = TeachX(2);
    var point2 = TeachX(3);
    var taught = new FlyScanRecipe("Manual teaching", [
        new("Front", "TCH1", [start1, point1], [new("T1", 1, point1)]),
        new("Back", "TCH2", [start2, point2], [new("T2", 1, point2)])]);
    string saved = Path.Combine(evidence, "taught-recipe.json");
    taught.Save(saved);
    var loaded = FlyScanRecipe.Load(saved);
    Check(loaded.Groups[0].Waypoints[1].X == 1 && loaded.Groups[1].Waypoints[1].X == 3 &&
        loaded.Groups[1].RobotPose == "TCH2", "manually captured feedback and robot pose identifiers survive save and reload");
    using var flow = new FlyScanWorkflow(machine);
    Reject<InvalidOperationException>(() => flow.Start(loaded, new()), "automatic scan cannot start in manual debug");
    machine.SetFeedback(20, 0);
    flow.Start(loaded, new());
    Drive(flow, () => flow.Phase == ScanPhase.Completed);
    Check(flow.Counts == new ScanCounts(1, 2, 2), "round-tripped taught recipe completes both groups");
    outputs["taught-run"] = flow.Events;

    ScanPoint TeachX(double value)
    {
        Require(machine.TryOperateAxis(x, "Absolute", out _, value), "manual teaching move");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (machine.ReadAxis(x) is { Busy: false, ActualPosition: var actual } && actual == value)
                return machine.CaptureTeachingPoint();
            Thread.Sleep(2);
        }
        throw new InvalidOperationException("Teaching axis did not settle.");
    }
}

using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine))
{
    flow.Start(recipe, new());
    var load = flow.Outputs.RequestLoad!.Value;
    flow.Tick(TimeSpan.FromMilliseconds(5), new(LoadConfirmation: load with { Workpiece = Guid.NewGuid() }, VisionReady: true));
    Check(flow.Phase == ScanPhase.AwaitLoad && !flow.TriggerArmed, "old load feedback cannot start a new workpiece");
    Step(flow);
    var pose1 = flow.Outputs.RequestRobot!;
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { Robot = new(Guid.NewGuid(), pose1.Pose, true, false) });
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { Robot = new(pose1.Command, "Wrong pose", true, false) });
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { Robot = new(pose1.Command, pose1.Pose, true, true) });
    Check(flow.Phase == ScanPhase.AwaitRobot && flow.Counts.Triggers == 0, "stale, wrong-pose and still-moving feedback cannot arm");
    Step(flow);
    flow.Tick(TimeSpan.FromMilliseconds(20), Inputs(flow) with { VisionReady = false });
    Check(flow.Phase == ScanPhase.AwaitReady && flow.Outputs.HoldRobot && !flow.TriggerArmed, "robot waits in position for vision Ready");
    Drive(flow, () => flow.ConfirmedGroups == 1);
    Check(flow.Outputs.RequestRobot is { Pose: "P02" } second && second.Command != pose1.Command,
        "robot gets a new pose request only after first group confirmation");
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { Robot = new(pose1.Command, pose1.Pose, true, false) });
    Check(flow.Phase == ScanPhase.AwaitRobot, "first robot confirmation cannot acknowledge the second group");
    Drive(flow, () => flow.Phase == ScanPhase.AwaitUnload);
    var requestedUnload = flow.Outputs.RequestUnload!.Value;
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { UnloadConfirmation = requestedUnload with { Workpiece = Guid.NewGuid() } });
    Check(flow.Counts == new ScanCounts(0, 2, 5), "piece requires its own unload confirmation after both groups");
    var unload = Inputs(flow);
    flow.Tick(TimeSpan.FromMilliseconds(5), unload);
    flow.Tick(TimeSpan.FromMilliseconds(5), unload);
    Check(flow.Counts == new ScanCounts(1, 2, 5) && !flow.Outputs.VisionTrigger, "repeat unload feedback does not duplicate the completed workpiece");
    outputs["handshake-run"] = flow.Events;
}

using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine))
{
    var groups = recipe.Groups.ToArray();
    // X=1 is crossed on segment 1 as well. Only the complete taught coordinate on segment 2 is a trigger.
    groups[0] = groups[0] with
    {
        Waypoints = [new(0, 0, 0), new(2, 0, 0), new(0, 2, 2), new(-2, 4, 4)],
        Triggers = [new("ThreeAxis", 2, new(1, 1, 1)), new("AfterCorner", 3, new(-1, 3, 3))]
    };
    flow.Start(recipe with { Groups = groups }, new());
    Drive(flow, () => flow.Phase == ScanPhase.Scanning);
    flow.Tick(TimeSpan.FromMilliseconds(15), Inputs(flow));
    Check(flow.Counts.Triggers == 0 && flow.Feedback is { Busy: true, Position.X: > 1 },
        "crossing X alone with incorrect Y/R1 produces no pulse");
    flow.Tick(TimeSpan.FromMilliseconds(180), Inputs(flow));
    var pulses = Pulses(flow);
    Check(flow.Phase == ScanPhase.ConfirmGroup && pulses.Length == 2 &&
        Close(pulses[0].Position, new(1, 1, 1)) && Close(pulses[1].Position, new(-1, 3, 3)) && pulses[0].Off < pulses[1].On,
        "one coarse tick crosses a corner and emits both three-axis pulses in time order");
    outputs["multidimensional-compare"] = flow.Events;
}

using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine))
{
    var pass = new List<object>();
    var peers = new SimulatedScanPeers();
    var expected = recipe.Groups.SelectMany(group => group.Triggers).ToArray();
    int movingSamples = 0;
    for (int run = 0; run < 100; run++)
    {
        flow.Start(recipe, new());
        Guid id = flow.Workpiece!.Value;
        var frames = new List<ScanMotionFeedback>();
        Drive(flow, () => flow.Phase == ScanPhase.Completed, () =>
        {
            var inputs = peers.Read(flow.Outputs, loaded: true, unloaded: true, robotResponds: true, ready: true,
                connected: true, alarm: false, emergency: false);
            if (flow.Outputs.HoldRobot)
                Require(inputs.Robot is { InPosition: true, Moving: false } && inputs.VisionReady, "robot hold and Ready during acquisition");
            flow.Tick(TimeSpan.FromMilliseconds(12), inputs);
            if (flow.Phase == ScanPhase.Scanning && flow.Feedback is { Busy: true } feedback) frames.Add(feedback);
        });
        var events = flow.Events.Where(item => item.Workpiece == id).ToArray();
        var actual = events.Where(item => item.Pulse is not null).Select(item => item.Pulse!).ToArray();
        Require(actual.Length == expected.Length && actual.Zip(expected).All(pair => pair.First.Name == pair.Second.Name &&
            Close(pair.First.Position, pair.Second.Position)), "pulse sequence and XYZ/R1 samples match independent targets");
        Require(actual.Zip(actual.Skip(1)).All(pair => pair.First.Off < pair.Second.On), "trigger pulses are ordered and non-overlapping");
        Require(frames.Count > 2 && frames.All(frame => Math.Abs(frame.Position.R1 - frame.R2) < 1e-7 &&
            Math.Abs(frame.R1Speed - frame.R2Speed) < 1e-7), "R2 follows R1 in displacement and velocity while moving");
        Require(events.Count(item => item.Kind == "组完成") == 2 && events.Count(item => item.Kind == "工件完成") == 1,
            "group and workpiece completion events");
        Require(flow.Counts == new ScanCounts(run + 1, (run + 1) * 2, (run + 1) * 5), "aggregate counts");
        movingSamples += frames.Count;
        pass.Add(new { workpiece = id, sequence = run + 1, frames, events });
    }
    Check(flow.Counts == new ScanCounts(100, 200, 500), "100 uninterrupted dual-group runs: 100 workpieces, 200 confirmed groups, 500 pulses");
    Check(movingSamples > 200, $"continuous interpolation and R2 synchronization verified at {movingSamples} moving samples");
    outputs["F1-100-runs"] = pass;
}

RunAbort("cancel-wait-load", ScanPhase.AwaitLoad, ScanInterruption.Cancelled, explicitCommand: true);
RunAbort("pause-wait-ready", ScanPhase.AwaitReady, ScanInterruption.Paused, explicitCommand: true);
RunAbort("stop-positioning", ScanPhase.Positioning, ScanInterruption.Stopped, explicitCommand: true, group: 1);
RunAbort("robot-withdraws-before-first-pulse", ScanPhase.Scanning, ScanInterruption.RobotMoved,
    change: inputs => inputs with { Robot = inputs.Robot! with { Moving = true } });
RunAbort("ready-loss-after-first-pulse", ScanPhase.Scanning, ScanInterruption.ReadyLost,
    change: inputs => inputs with { VisionReady = false }, partial: true);
RunAbort("disconnect-at-group-end", ScanPhase.ConfirmGroup, ScanInterruption.Disconnected,
    change: inputs => inputs with { CardConnected = false, BlockCleanup = true }, pending: true);
RunAbort("timeout-at-await-robot", ScanPhase.AwaitRobot, ScanInterruption.Timeout,
    change: inputs => inputs with { Robot = null }, elapsed: TimeSpan.FromSeconds(30));
RunAbort("alarm-in-second-scan", ScanPhase.Scanning, ScanInterruption.Alarm,
    change: inputs => inputs with { Alarm = true }, group: 1, partial: true);
RunAbort("emergency-at-unload", ScanPhase.AwaitUnload, ScanInterruption.Emergency,
    change: inputs => inputs with { EmergencyStop = true }, group: 2);

VerifyTriggerBoundaries();
VerifyGroupConfirmationBoundary();
VerifyScanOwnership();
VerifyTransferRecovery();

using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine))
{
    var recording = new ScanInputRecording(recipe);
    recording.Start(flow, new());
    DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Scanning);
    recording.SetRole(flow, machine, null);
    recording.Tick(flow, TimeSpan.FromMilliseconds(12), Inputs(flow));
    Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.ContextChanged &&
        !flow.TriggerArmed && flow.Counts.Triggers == 0, "role loss revokes automatic motion before dispatching more pulses");
    recording.SetRole(flow, machine, UserRole.EquipmentEngineer);
    recording.Recover(flow, new());
    DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
    Check(flow.Counts == new ScanCounts(1, 2, 5), "reauthorized recovery completes exactly one piece");
    outputs["role-change"] = flow.Events;
    SaveReplay(recording, "role-change");
}

string journal = Path.Combine(evidence, "restart-journal.json");
Guid persistentId;
ScanCounts persisted;
FlyScanInputs completedFeedback;
using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine, journal))
{
    Reject<IOException>(() => new FlyScanWorkflow(machine, journal).Dispose(), "a second session cannot take the same journal");
    flow.Start(recipe, new());
    Drive(flow, () => flow.Phase == ScanPhase.Scanning && flow.ConfirmedGroups == 1 && flow.LastConfirmedTrigger == 1);
    persistentId = flow.Workpiece!.Value;
    persisted = flow.Counts;
    outputs["before-process-restart"] = flow.Events;
}
using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine, journal))
{
    Step(flow);
    Check(flow.Phase == ScanPhase.Interrupted && flow.Workpiece == persistentId && flow.LastConfirmedTrigger == 1 &&
        flow.ConfirmedGroups == 1 && flow.Counts == persisted && !flow.TriggerArmed && !flow.Outputs.VisionTrigger,
        "new controller session restores last confirmed pulse and group without replay");
    flow.Recover(new());
    Drive(flow, () => flow.Phase == ScanPhase.AwaitUnload);
    completedFeedback = Inputs(flow);
    flow.Tick(TimeSpan.FromMilliseconds(12), completedFeedback);
    Check(flow.Attempt == 2 && flow.Counts == new ScanCounts(1, 2, 6) &&
        flow.Events.Where(item => item.Pulse is not null).All(item => item.Group == 2 && item.Attempt == 2),
        "restart recovery repeats only the unfinished group and counts the piece once");
    outputs["after-process-restart"] = flow.Events;
}
using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine, journal))
{
    flow.Tick(TimeSpan.FromSeconds(1), completedFeedback);
    Check(flow.Phase == ScanPhase.Completed && flow.Counts == new ScanCounts(1, 2, 6),
        "completed checkpoint remains counted only once across a second restart");
}

string trace = Path.Combine(evidence, "flight-traces.json");
File.WriteAllText(trace, JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true }));
HistoryChecks.Verify(evidence, profile, recipe, Check);
await ProcessRestartChecks.VerifyAsync(evidence, replayDirectory, recipe, Check);
var replays = ScanInputRecording.ReplayDirectory(replayDirectory);
foreach (var replay in replays)
    Check(replay.Steps > 0 && replay.Counts.Pieces == 1, $"recorded inputs replay {replay.Scenario} to one completed piece");
File.WriteAllText(Path.Combine(replayDirectory, "report.json"),
    JsonSerializer.Serialize(new { Status = "passed", Scenarios = replays }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"FlyScan workflow smoke passed. {checks} checks. Trace: {trace}");

SimulatedMachine CreateAutomaticMachine() => ScanInputRecording.CreateMachine(profile);

void SaveReplay(ScanInputRecording recording, string name) =>
    recording.Save(Path.Combine(replayDirectory, $"{name}.trace.json"));

void VerifyTriggerBoundaries()
{
    foreach (bool edgeFirst in new[] { false, true })
    {
        using var machine = CreateAutomaticMachine();
        using var flow = new FlyScanWorkflow(machine);
        using var monitor = new SimulatedIoMonitor(machine);
        monitor.SelectedSignalType = "DO";
        monitor.SetViewVisibleAsync(true).GetAwaiter().GetResult();
        var boundary = BoundaryRecipe(machine, secondTriggerAtMs: 25);
        var recording = new ScanInputRecording(boundary);
        recording.Start(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Scanning);
        var connectionEpoch = monitor.ScanConnectionEpoch;
        var crossingInputs = Inputs(flow);
        var crossing = TimeSpan.FromMilliseconds(10.5);
        if (edgeFirst)
        {
            recording.Tick(flow, crossing, crossingInputs);
            Check(flow.Outputs.VisionTrigger && Pulses(flow) is [{ Name: "First" } first] &&
                first.On < flow.Clock && first.Off > flow.Clock,
                "the first trigger has already risen and is still high when Ready is lost");
            monitor.PublishScanIo(connectionEpoch, crossingInputs, flow.Outputs);
            Check(monitor.Items[3] is { DisplayName: "视觉触发", Value: true },
                "IO monitor reads a high trigger from the current workflow output");
        }

        var interruptedAt = flow.Clock;
        var faultInputs = crossingInputs with { VisionReady = false };
        var faultStep = edgeFirst ? TimeSpan.Zero : crossing;
        recording.Tick(flow, faultStep, faultInputs);
        int confirmedPulses = edgeFirst ? 1 : 0;
        Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.ReadyLost &&
            flow.Counts == new ScanCounts(0, 0, confirmedPulses) && flow.LastConfirmedTrigger == confirmedPulses &&
            !flow.TriggerArmed && flow.Outputs == new FlyScanOutputs(null, null, false, null, false),
            $"Ready loss with edge-first={edgeFirst} withdraws all requests and retains only emitted edges");
        var interruptedPulses = Pulses(flow);
        monitor.PublishScanIo(connectionEpoch, faultInputs, flow.Outputs);
        Check(monitor.Items[3] is { DisplayName: "视觉触发", Value: false } &&
            monitor.Items[2] is { DisplayName: "机器人保持请求", Value: false },
            "IO monitor reads cleared trigger and hold from the interrupted workflow");
        Check(edgeFirst
                ? interruptedPulses is [{ Off: var off }] && off == interruptedAt
                : interruptedPulses.Length == 0,
            $"Ready loss with edge-first={edgeFirst} leaves an accurate pulse trace");
        recording.Tick(flow, TimeSpan.FromMilliseconds(12), Inputs(flow));
        Check(flow.Phase == ScanPhase.Interrupted && flow.Counts.Triggers == confirmedPulses,
            "restoring Ready does not restart a scan");
        recording.Recover(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
        Check(flow.Counts == new ScanCounts(1, 2, confirmedPulses + 4) &&
            flow.Events.Where(item => item.Attempt == 2 && item.Pulse is not null)
                .Select(item => item.Pulse!.Name).SequenceEqual(["First", "Second", "B1", "B2"]),
            "operator recovery repeats the interrupted group from its start and settles once");
        string name = edgeFirst ? "trigger-before-ready-loss" : "ready-loss-before-trigger";
        SaveReplay(recording, name);
        outputs[name] = new
        {
            recipe = boundary,
            crossingInputs,
            crossingElapsedTicks = edgeFirst ? crossing.Ticks : 0,
            faultInputs,
            faultElapsedTicks = faultStep.Ticks,
            interruptedAt,
            events = flow.Events
        };
    }

    foreach (double secondAtMs in new[] { 12.0, 12.5 })
    {
        using var machine = CreateAutomaticMachine();
        using var flow = new FlyScanWorkflow(machine);
        var boundary = BoundaryRecipe(machine, secondAtMs);
        flow.Start(boundary, new());
        Drive(flow, () => flow.Phase == ScanPhase.Scanning);
        var scanInputs = Inputs(flow);
        var scanStep = TimeSpan.FromMilliseconds(20);
        flow.Tick(scanStep, scanInputs);
        var pulses = Pulses(flow);
        if (secondAtMs == 12)
            Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.MotionFailed &&
                !flow.TriggerArmed && flow.Counts == new ScanCounts(0, 0, 1) && pulses is [{ Name: "First" }],
                "the next compare at the falling-edge timestamp cannot count as a separate pulse");
        else
            Check(flow.Phase == ScanPhase.Scanning && flow.Counts == new ScanCounts(0, 0, 2) &&
                pulses is [{ Name: "First" } first, { Name: "Second" } second] &&
                second.On - first.Off == TimeSpan.FromMilliseconds(0.5),
                "two comparison points with a positive low interval emit two distinct pulses");
        outputs[secondAtMs == 12 ? "coincident-falling-edge" : "separate-rising-edge"] = new
        {
            recipe = boundary, scanInputs, elapsedTicks = scanStep.Ticks, events = flow.Events
        };
    }

    FlyScanRecipe BoundaryRecipe(SimulatedMachine machine, double secondTriggerAtMs)
    {
        double speed = machine.Coordinates.Single(item => item.CoordinateId == new CoordinateId("Front.Scan")).LinearVelocity;
        ScanPoint At(double milliseconds) => new(speed * milliseconds / 1000, 0, 0);
        var first = recipe.Groups[0] with
        {
            Waypoints = [At(0), At(60)],
            Triggers = [new("First", 1, At(10)), new("Second", 1, At(secondTriggerAtMs))]
        };
        return recipe with { Name = "Trigger boundary", Groups = [first, recipe.Groups[1]] };
    }
}

void VerifyGroupConfirmationBoundary()
{
    foreach (bool confirmFirst in new[] { false, true })
    {
        using var machine = CreateAutomaticMachine();
        using var flow = new FlyScanWorkflow(machine);
        var recording = new ScanInputRecording(recipe);
        recording.Start(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.ConfirmGroup);
        var confirmationInputs = Inputs(flow);
        var step = TimeSpan.FromMilliseconds(12);
        if (confirmFirst) recording.Tick(flow, step, confirmationInputs);
        var alarmInputs = Inputs(flow) with { Alarm = true };
        recording.Tick(flow, step, alarmInputs);
        int confirmedGroups = confirmFirst ? 1 : 0;
        Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.Alarm &&
            flow.Counts == new ScanCounts(0, confirmedGroups, 3) && flow.ConfirmedGroups == confirmedGroups,
            $"alarm with confirmation-first={confirmFirst} preserves only groups already confirmed");
        recording.Recover(flow, new());
        DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
        var expected = recipe.Groups.Skip(confirmedGroups).SelectMany(item => item.Triggers).Select(item => item.Name).ToArray();
        Check(flow.Counts == new ScanCounts(1, 2, 3 + expected.Length) &&
            flow.Events.Where(item => item.Attempt == 2 && item.Pulse is not null)
                .Select(item => item.Pulse!.Name).SequenceEqual(expected),
            $"recovery with confirmation-first={confirmFirst} repeats only unconfirmed groups and settles once");
        string name = confirmFirst ? "group-confirmation-before-alarm" : "alarm-before-group-confirmation";
        SaveReplay(recording, name);
        outputs[name] = new
        {
            confirmationInputs,
            confirmationElapsedTicks = confirmFirst ? step.Ticks : 0,
            alarmInputs,
            alarmElapsedTicks = step.Ticks,
            events = flow.Events
        };
    }
}

void VerifyScanOwnership()
{
    foreach (bool recovering in new[] { false, true })
    {
        using var machine = CreateAutomaticMachine();
        using var flow = new FlyScanWorkflow(machine);
        if (recovering)
        {
            flow.Start(recipe, new());
            flow.Interrupt(ScanInterruption.Paused, new());
        }

        bool revoked = false;
        machine.PropertyChanged += (_, _) =>
        {
            if (revoked || machine.Status != 50) return;
            revoked = true;
            if (recovering) machine.LoseFeedback();
            else machine.SelectRole(null);
        };
        if (recovering) flow.Recover(new());
        else flow.Start(recipe, new());

        Check(revoked && flow.Outputs == new FlyScanOutputs(null, null, false, null, false),
            $"a permission change while publishing running status hides all stale requests, recovering={recovering}");
        Step(flow);
        Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.ContextChanged &&
            flow.Counts == new ScanCounts(0, 0, 0) && !flow.TriggerArmed,
            $"a change during running notification invalidates the dispatched scan, recovering={recovering}");
        outputs[recovering ? "feedback-loss-at-recover" : "role-change-at-start"] = flow.Events;
    }

    using (var machine = CreateAutomaticMachine())
    using (var interrupted = new FlyScanWorkflow(machine))
    using (var active = new FlyScanWorkflow(machine))
    {
        interrupted.Start(recipe, new());
        interrupted.Interrupt(ScanInterruption.Paused, new());
        active.Start(recipe, new());
        Drive(active, () => active.Phase == ScanPhase.Scanning && active.Counts.Triggers == 1);
        var activeId = active.Workpiece;
        Reject<InvalidOperationException>(() => interrupted.Recover(new()),
            "an older interrupted workflow cannot cancel or take over the live scan");
        interrupted.Dispose();
        Drive(active, () => active.Phase == ScanPhase.Completed);
        Check(active.Workpiece == activeId && active.Counts == new ScanCounts(1, 2, 5),
            "disposing the old workflow cannot clear the current path or alter its count");
        outputs["active-scan-rejects-other-workflow"] = active.Events;
    }

    using (var machine = CreateAutomaticMachine())
    using (var flow = new FlyScanWorkflow(machine))
    {
        flow.Start(recipe, new());
        Drive(flow, () => flow.Phase == ScanPhase.Scanning);
        flow.Tick(TimeSpan.Zero, Inputs(flow) with { CardConnected = false });
        machine.SetFeedback(0, 30);
        var x = new AxisId("Front.X");
        Check(flow.CleanupPending && !machine.CanAdjust && !machine.TryOperateAxis(x, "Positive", out _),
            "manual debug cannot take axes while the old disconnected scan still needs cleanup");
        machine.SetFeedback(20, 0);
        flow.Recover(new());
        Drive(flow, () => flow.Phase == ScanPhase.Completed);
        Check(!flow.CleanupPending && flow.Counts == new ScanCounts(1, 2, 5),
            "only the interrupted workflow resumes after its own path cleanup");
        outputs["manual-blocked-before-scan-cleanup"] = flow.Events;
    }

    using (var machine = CreateAutomaticMachine())
    {
        using (var closing = new FlyScanWorkflow(machine))
        {
            closing.Start(recipe, new());
            Drive(closing, () => closing.Phase == ScanPhase.Scanning && closing.Counts.Triggers == 1);
        }
        using var next = new FlyScanWorkflow(machine);
        Check(next.CanStart, "closing a simulated owner clears its path and returns control of the axes");
        next.Start(recipe, new());
        Drive(next, () => next.Phase == ScanPhase.Completed);
        Check(next.Counts == new ScanCounts(1, 2, 5), "the next workflow starts without an orphaned path");
        outputs["disposed-owner-releases-axes"] = next.Events;
    }

    using (var machine = CreateAutomaticMachine())
    {
        var path = Path.Combine(evidence, "ownership-start-blocked.json");
        using var flow = new FlyScanWorkflow(machine, path);
        using var deniedReplacement = File.Create(path);
        bool runningPublished = false;
        machine.PropertyChanged += (_, _) => runningPublished |= machine.Status == 50;
        bool rejected = false;
        try { flow.Start(recipe, new()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
        Check(rejected, "a failed initial checkpoint rejects the scan start");
        Check(!runningPublished && flow.Phase == ScanPhase.Idle && flow.CanStart && machine.Status == 0,
            "a failed initial checkpoint neither publishes running nor retains the axes");
    }
}

void VerifyTransferRecovery()
{
    foreach (var phase in new[] { ScanPhase.AwaitLoad, ScanPhase.AwaitUnload })
    foreach (bool restart in new[] { false, true })
    {
        string name = $"transfer-{phase}-{(restart ? "restart" : "interruption")}";
        string path = Path.Combine(evidence, name + ".json");
        using var machine = CreateAutomaticMachine();
        using var original = new FlyScanWorkflow(machine, path);
        var recording = new ScanInputRecording(recipe);
        recording.Start(original, new());
        DriveRecorded(original, recording, () => original.Phase == phase);
        var previous = original.Outputs;
        var stale = Inputs(original);
        var counts = original.Counts;
        var piece = original.Workpiece;

        if (restart)
        {
            original.Dispose();
            using var restartedMachine = CreateAutomaticMachine();
            using var restarted = new FlyScanWorkflow(restartedMachine, path);
            recording.Restarted(restarted);
            VerifyRecovery(restarted);
        }
        else
        {
            recording.Tick(original, TimeSpan.Zero, stale with { EmergencyStop = true });
            VerifyRecovery(original);
        }

        void VerifyRecovery(FlyScanWorkflow flow)
        {
            Check(flow.Phase == ScanPhase.Interrupted && flow.Workpiece == piece && flow.Counts == counts,
                $"{name}: interruption preserves the workpiece but does not accept the pending acknowledgement");
            recording.Recover(flow, stale);
            var fresh = flow.Outputs;
            recording.Tick(flow, TimeSpan.FromMilliseconds(5), stale);
            Check(flow.Phase == phase && flow.Workpiece == piece && flow.Counts == counts,
                $"{name}: the previous request's acknowledgement cannot advance a recovered workpiece");
            Check(phase == ScanPhase.AwaitLoad
                ? fresh.RequestLoad is { Workpiece: var loaded } && loaded == piece && fresh.RequestLoad != previous.RequestLoad
                : fresh.RequestUnload is { Workpiece: var unloaded } && unloaded == piece && fresh.RequestUnload != previous.RequestUnload,
                $"{name}: recovery issues a fresh transfer request");
            DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
            recording.Tick(flow, TimeSpan.FromMilliseconds(5), stale);
            Check(flow.Workpiece == piece && flow.Attempt == 2 && flow.Counts == new ScanCounts(1, 2, 5) &&
                (phase != ScanPhase.AwaitUnload || flow.Events.All(entry => entry.Attempt != 2 || entry.Pulse is null)),
                $"{name}: a new acknowledgement settles the same workpiece once and preserves any completed groups");
            outputs[name] = new { previous, fresh, Workpiece = piece, Events = flow.Events };
            SaveReplay(recording, name);
        }
    }
}

void RunAbort(string name, ScanPhase phase, ScanInterruption reason, bool explicitCommand = false,
    Func<FlyScanInputs, FlyScanInputs>? change = null, int group = 0, bool partial = false, bool pending = false, TimeSpan? elapsed = null)
{
    using var machine = CreateAutomaticMachine();
    using var flow = new FlyScanWorkflow(machine);
    var recording = new ScanInputRecording(recipe);
    recording.Start(flow, new());
    DriveRecorded(flow, recording, () => flow.Phase == phase && flow.ConfirmedGroups == group && (!partial || flow.LastConfirmedTrigger > 0));
    var counts = flow.Counts;
    Guid id = flow.Workpiece!.Value;
    var inputs = Inputs(flow);
    if (explicitCommand) recording.Interrupt(flow, reason, inputs);
    else recording.Tick(flow, elapsed ?? TimeSpan.FromSeconds(1), change!(inputs));
    Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == reason && !flow.Outputs.VisionTrigger &&
        !flow.TriggerArmed && flow.Outputs.RequestRobot is null && flow.Counts == counts && flow.CleanupPending == pending,
        $"{name}: withdraws trigger and never confirms work or emits pulses after the fault");
    Require(pending ? flow.Feedback is null : flow.Feedback is { Busy: false, R1Speed: 0, R2Speed: 0 },
        "cleanup distinguishes unavailable feedback from a confirmed stop");
    recording.Tick(flow, TimeSpan.FromMilliseconds(12), Inputs(flow));
    Require(flow.Counts == counts && flow.Phase == ScanPhase.Interrupted, "signal restoration does not auto-resume");
    if (pending)
    {
        recording.RejectRecovery(flow, new(BlockCleanup: true));
        Check(flow.CleanupPending && flow.Phase == ScanPhase.Interrupted, $"{name}: failed cleanup keeps recovery blocked");
    }
    recording.Recover(flow, new());
    DriveRecorded(flow, recording, () => flow.Phase == ScanPhase.Completed);
    int expectedPulses = counts.Triggers + recipe.Groups.Skip(group).Sum(item => item.Triggers.Count);
    Check(flow.Workpiece == id && flow.Attempt == 2 && flow.Counts == new ScanCounts(1, 2, expectedPulses) &&
        flow.Events.Where(item => item.Pulse is not null && item.Attempt == 2).All(item => item.Group > group),
        $"{name}: explicit recovery preserves completed groups and settles the piece once");
    outputs[name] = flow.Events;
    SaveReplay(recording, name);
}

static FlyScanInputs Inputs(FlyScanWorkflow flow)
{
    var outputs = flow.Outputs;
    return new FlyScanInputs(outputs.RequestLoad, outputs.RequestRobot is { } request
        ? new RobotPoseFeedback(request.Command, request.Pose, true, false) : null, true, outputs.RequestUnload);
}
static void Step(FlyScanWorkflow flow) => flow.Tick(TimeSpan.FromMilliseconds(12), Inputs(flow));
static void DriveRecorded(FlyScanWorkflow flow, ScanInputRecording recording, Func<bool> done) =>
    Drive(flow, done, () => recording.Tick(flow, TimeSpan.FromMilliseconds(12), Inputs(flow)));
static void Drive(FlyScanWorkflow flow, Func<bool> done, Action? step = null)
{
    for (int i = 0; i < 400 && !done(); i++)
    {
        Require(flow.IsRunning, $"unexpected phase {flow.Phase}: {flow.Message}");
        if (step is null) Step(flow);
        else step();
    }
    Require(done(), $"condition not reached: {flow.Phase}, confirmed groups {flow.ConfirmedGroups}");
}
static TriggerPulse[] Pulses(FlyScanWorkflow flow) =>
    flow.Events.Where(item => item.Pulse is not null).Select(item => item.Pulse!).ToArray();
static bool Close(ScanPoint a, ScanPoint b) => Math.Abs(a.X - b.X) < 1e-7 &&
    Math.Abs(a.Y - b.Y) < 1e-7 && Math.Abs(a.R1 - b.R1) < 1e-7;
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    Check(false, message);
}
void Check(bool result, string message)
{
    Require(result, message);
    checks++;
    Console.WriteLine($"PASS {message}");
}
static void Require(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
}

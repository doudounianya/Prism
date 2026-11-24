using Company.FlyScan.Runtime;
using Company.Hardware.ControlCard;
using Company.Platform.Shared.Login;
using System.Diagnostics;
using System.Text.Json;

var profile = Path.Combine(AppContext.BaseDirectory, "FlyScan.13Axes.json");
var recipe = FlyScanRecipe.Load(Path.Combine(AppContext.BaseDirectory, "FlyScan.DemoRecipe.json"));
if (args is ["--interrupt-process" or "--recover-process", var restartPath])
{
    using var machine = new SimulatedMachine(profile);
    machine.SelectRole(UserRole.EquipmentEngineer);
    machine.SetFeedback(20, 0);
    using var history = new FlyScanHistory(Path.ChangeExtension(restartPath, ".sqlite"));
    using var flow = new FlyScanWorkflow(machine, restartPath, history: history);
    if (args[0] == "--interrupt-process")
    {
        Require(flow.Phase == ScanPhase.Idle, "restart proof requires a new checkpoint");
        flow.Start(recipe, new());
        Drive(flow, () => flow.Phase == ScanPhase.Scanning && flow.ConfirmedGroups == 1 && flow.LastConfirmedTrigger == 1);
        Console.WriteLine($"PROCESS_CHECKPOINT {flow.Workpiece} {flow.Counts}");
        Console.Out.Flush();
        // End this test process without sending stop or running shutdown handlers.
        Environment.Exit(0);
    }
    else
    {
        Require(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.Restarted &&
            flow.ConfirmedGroups == 1 && flow.LastConfirmedTrigger == 1 && !flow.Outputs.VisionTrigger, "restart gates replay");
        var id = flow.Workpiece;
        flow.Recover(new());
        Drive(flow, () => flow.Phase == ScanPhase.Completed);
        Require(flow.Workpiece == id && flow.Counts == new ScanCounts(1, 2, 6), "restart settles same piece once");
        Require(history.Pieces(history.Now.AddDays(-1), history.Now.AddTicks(1)) is [{ Id: var savedId }] && savedId == id,
            "G4 history persists exactly one completion across processes");
        Console.WriteLine($"PASS PROCESS_RECOVERED {id} {flow.Counts}");
    }
    return;
}
var evidence = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "flyscan-workflow-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);
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
    flow.Tick(TimeSpan.FromMilliseconds(5), new(LoadedWorkpiece: Guid.NewGuid(), VisionReady: true));
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
    flow.Tick(TimeSpan.FromMilliseconds(5), Inputs(flow) with { UnloadedWorkpiece = Guid.NewGuid() });
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

using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine))
{
    flow.Start(recipe, new());
    Drive(flow, () => flow.Phase == ScanPhase.Scanning);
    machine.SelectRole(null);
    Step(flow);
    Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == ScanInterruption.ContextChanged &&
        !flow.TriggerArmed && flow.Counts.Triggers == 0, "role loss revokes automatic motion before dispatching more pulses");
    machine.SelectRole(UserRole.EquipmentEngineer);
    flow.Recover(new());
    Drive(flow, () => flow.Phase == ScanPhase.Completed);
    Check(flow.Counts == new ScanCounts(1, 2, 5), "reauthorized recovery completes exactly one piece");
    outputs["role-change"] = flow.Events;
}

string journal = Path.Combine(evidence, "restart-journal.json");
Guid persistentId;
ScanCounts persisted;
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
    Drive(flow, () => flow.Phase == ScanPhase.Completed);
    Check(flow.Attempt == 2 && flow.Counts == new ScanCounts(1, 2, 6) &&
        flow.Events.Where(item => item.Pulse is not null).All(item => item.Group == 2 && item.Attempt == 2),
        "restart recovery repeats only the unfinished group and counts the piece once");
    outputs["after-process-restart"] = flow.Events;
}
using (var machine = CreateAutomaticMachine())
using (var flow = new FlyScanWorkflow(machine, journal))
{
    flow.Tick(TimeSpan.FromSeconds(1), new(UnloadedWorkpiece: persistentId, VisionReady: true));
    Check(flow.Phase == ScanPhase.Completed && flow.Counts == new ScanCounts(1, 2, 6),
        "completed checkpoint remains counted only once across a second restart");
}

string trace = Path.Combine(evidence, "flight-traces.json");
File.WriteAllText(trace, JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true }));
HistoryChecks.Verify(evidence, profile, recipe, Check);
Console.WriteLine($"FlyScan workflow smoke passed. {checks} checks. Trace: {trace}");

SimulatedMachine CreateAutomaticMachine()
{
    var machine = new SimulatedMachine(profile);
    machine.SelectRole(UserRole.EquipmentEngineer);
    machine.SetFeedback(20, 0);
    return machine;
}

void RunAbort(string name, ScanPhase phase, ScanInterruption reason, bool explicitCommand = false,
    Func<FlyScanInputs, FlyScanInputs>? change = null, int group = 0, bool partial = false, bool pending = false, TimeSpan? elapsed = null)
{
    using var machine = CreateAutomaticMachine();
    using var flow = new FlyScanWorkflow(machine);
    flow.Start(recipe, new());
    Drive(flow, () => flow.Phase == phase && flow.ConfirmedGroups == group && (!partial || flow.LastConfirmedTrigger > 0));
    var counts = flow.Counts;
    Guid id = flow.Workpiece!.Value;
    var inputs = Inputs(flow);
    if (explicitCommand) flow.Interrupt(reason, inputs);
    else flow.Tick(elapsed ?? TimeSpan.FromSeconds(1), change!(inputs));
    Check(flow.Phase == ScanPhase.Interrupted && flow.Interruption == reason && !flow.Outputs.VisionTrigger &&
        !flow.TriggerArmed && flow.Outputs.RequestRobot is null && flow.Counts == counts && flow.CleanupPending == pending,
        $"{name}: withdraws trigger and never confirms work or emits pulses after the fault");
    Require(pending ? flow.Feedback is null : flow.Feedback is { Busy: false, R1Speed: 0, R2Speed: 0 },
        "cleanup distinguishes unavailable feedback from a confirmed stop");
    Step(flow);
    Require(flow.Counts == counts && flow.Phase == ScanPhase.Interrupted, "signal restoration does not auto-resume");
    if (pending)
        Reject<InvalidOperationException>(() => flow.Recover(new(BlockCleanup: true)), $"{name}: failed cleanup keeps recovery blocked");
    flow.Recover(new());
    Drive(flow, () => flow.Phase == ScanPhase.Completed);
    int expectedPulses = counts.Triggers + recipe.Groups.Skip(group).Sum(item => item.Triggers.Count);
    Check(flow.Workpiece == id && flow.Attempt == 2 && flow.Counts == new ScanCounts(1, 2, expectedPulses) &&
        flow.Events.Where(item => item.Pulse is not null && item.Attempt == 2).All(item => item.Group > group),
        $"{name}: explicit recovery preserves completed groups and settles the piece once");
    outputs[name] = flow.Events;
}

static FlyScanInputs Inputs(FlyScanWorkflow flow)
{
    var outputs = flow.Outputs;
    return new FlyScanInputs(outputs.RequestLoad, outputs.RequestRobot is { } request
        ? new RobotPoseFeedback(request.Command, request.Pose, true, false) : null, true, outputs.RequestUnload);
}
static void Step(FlyScanWorkflow flow) => flow.Tick(TimeSpan.FromMilliseconds(12), Inputs(flow));
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

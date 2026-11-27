using System.Text.Json;
using System.Text.Json.Serialization;
using Company.FlyScan.Runtime;
using Company.Platform.Shared.Login;

internal sealed class ScanInputRecording(FlyScanRecipe recipe)
{
    private enum ActionKind { Start, Tick, Interrupt, Recover, RejectedRecovery, Role, Restart }
    private sealed record Pulse(int Group, int Attempt, string Name, ScanPoint Position, long OnTicks, long HighTicks);
    private sealed record State(Guid? Workpiece, ScanPhase Phase, ScanInterruption? Interruption, int Attempt,
        int ConfirmedGroups, int LastTrigger, ScanCounts Counts, bool CleanupPending, bool TriggerArmed,
        ScanMotionFeedback? Feedback, FlyScanOutputs Outputs, Pulse[] Pulses);
    private sealed record Step(ActionKind Kind, State After, FlyScanInputs? Inputs = null, long ElapsedTicks = 0,
        ScanInterruption? Reason = null, UserRole? Role = null);
    private sealed record Recording(int Version, FlyScanRecipe Recipe, Step[] Steps);
    public sealed record ReplayResult(string Scenario, int Steps, ScanCounts Counts);

    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly List<Step> _steps = [];

    public void Start(FlyScanWorkflow flow, FlyScanInputs inputs)
    {
        flow.Start(recipe, inputs);
        _steps.Add(new(ActionKind.Start, Observe(flow), inputs));
    }

    public void Tick(FlyScanWorkflow flow, TimeSpan elapsed, FlyScanInputs inputs)
    {
        flow.Tick(elapsed, inputs);
        _steps.Add(new(ActionKind.Tick, Observe(flow), inputs, elapsed.Ticks));
    }

    public void Interrupt(FlyScanWorkflow flow, ScanInterruption reason, FlyScanInputs inputs)
    {
        flow.Interrupt(reason, inputs);
        _steps.Add(new(ActionKind.Interrupt, Observe(flow), inputs, Reason: reason));
    }

    public void Recover(FlyScanWorkflow flow, FlyScanInputs inputs)
    {
        flow.Recover(inputs);
        _steps.Add(new(ActionKind.Recover, Observe(flow), inputs));
    }

    public void RejectRecovery(FlyScanWorkflow flow, FlyScanInputs inputs)
    {
        RequireRejectedRecovery(flow, inputs);
        _steps.Add(new(ActionKind.RejectedRecovery, Observe(flow), inputs));
    }

    public void SetRole(FlyScanWorkflow flow, SimulatedMachine machine, UserRole? role)
    {
        machine.SelectRole(role);
        _steps.Add(new(ActionKind.Role, Observe(flow), Role: role));
    }

    public void Restarted(FlyScanWorkflow flow) => _steps.Add(new(ActionKind.Restart, Observe(flow)));

    public void Save(string path) => Write(path, new Recording(FormatVersion, recipe, _steps.ToArray()));

    public static void Join(string initial, string resumed, string destination)
    {
        var before = Read(initial);
        var after = Read(resumed);
        if (before.Steps[0].Kind != ActionKind.Start || after.Steps[0].Kind != ActionKind.Restart)
            throw new InvalidDataException("The restart recording requires a start and a checkpoint reopen.");
        Write(destination, before with { Steps = [.. before.Steps, .. after.Steps] });
    }

    public static ReplayResult[] ReplayDirectory(string directory)
    {
        var paths = Directory.GetFiles(directory, "*.trace.json").Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("The replay directory contains no input recordings.");
        string profile = Path.Combine(directory, "axes.json");
        return paths.Select(path => Replay(path, profile)).ToArray();
    }

    public static SimulatedMachine CreateMachine(string profile)
    {
        var machine = new SimulatedMachine(profile);
        machine.SelectRole(UserRole.EquipmentEngineer);
        machine.SetFeedback(20, 0);
        return machine;
    }

    private static ReplayResult Replay(string path, string profile)
    {
        var recording = Read(path);
        string scenario = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        var scratch = Directory.CreateTempSubdirectory("flyscan-inputs-");
        string journal = Path.Combine(scratch.FullName, "checkpoint.json");
        var machine = CreateMachine(profile);
        var flow = new FlyScanWorkflow(machine, journal);
        var identifiers = new Dictionary<Guid, Guid>();
        var assigned = new HashSet<Guid>();
        int index = 0;
        try
        {
            foreach (var step in recording.Steps)
            {
                index++;
                var inputs = step.Inputs is { } recorded
                    ? recorded with
                    {
                        LoadConfirmation = MapTransfer(recorded.LoadConfirmation),
                        Robot = recorded.Robot is { } robot ? robot with { Command = Map(robot.Command) } : null,
                        UnloadConfirmation = MapTransfer(recorded.UnloadConfirmation),
                    }
                    : new FlyScanInputs();
                switch (step.Kind)
                {
                    case ActionKind.Start: flow.Start(recording.Recipe, inputs); break;
                    case ActionKind.Tick: flow.Tick(TimeSpan.FromTicks(step.ElapsedTicks), inputs); break;
                    case ActionKind.Interrupt: flow.Interrupt(step.Reason!.Value, inputs); break;
                    case ActionKind.Recover: flow.Recover(inputs); break;
                    case ActionKind.RejectedRecovery: RequireRejectedRecovery(flow, inputs); break;
                    case ActionKind.Role: machine.SelectRole(step.Role); break;
                    case ActionKind.Restart:
                        flow.Dispose();
                        machine.Dispose();
                        machine = CreateMachine(profile);
                        flow = new FlyScanWorkflow(machine, journal);
                        break;
                    default: throw new InvalidDataException($"Unknown scan input action {step.Kind}.");
                }
                var actual = Observe(flow);
                Bind(step.After.Workpiece, actual.Workpiece);
                Bind(step.After.Outputs.RequestLoad?.Command, actual.Outputs.RequestLoad?.Command);
                Bind(step.After.Outputs.RequestRobot?.Command, actual.Outputs.RequestRobot?.Command);
                Bind(step.After.Outputs.RequestUnload?.Command, actual.Outputs.RequestUnload?.Command);
                var comparable = step.After with
                {
                    Workpiece = step.After.Workpiece is { } workpiece ? Map(workpiece) : null,
                    Outputs = step.After.Outputs with
                    {
                        RequestLoad = MapTransfer(step.After.Outputs.RequestLoad),
                        RequestRobot = step.After.Outputs.RequestRobot is { } request ? request with { Command = Map(request.Command) } : null,
                        RequestUnload = MapTransfer(step.After.Outputs.RequestUnload),
                    },
                    Pulses = actual.Pulses,
                };
                if (actual != comparable || !actual.Pulses.SequenceEqual(step.After.Pulses))
                    throw new InvalidDataException($"Replay {scenario} step {index} ({step.Kind}) differs from the recorded state or pulses.");
            }
            return new ReplayResult(scenario, index, flow.Counts);
        }
        finally
        {
            flow.Dispose();
            machine.Dispose();
            scratch.Delete(recursive: true);
        }

        Guid Map(Guid original) => identifiers.GetValueOrDefault(original, original);

        TransferRequest? MapTransfer(TransferRequest? transfer) => transfer is { } value
            ? new TransferRequest(Map(value.Command), Map(value.Workpiece))
            : null;

        void Bind(Guid? recorded, Guid? current)
        {
            if (recorded is { } original && current is { } id && !identifiers.ContainsKey(original))
            {
                if (!assigned.Add(id))
                    throw new InvalidDataException($"Replay {scenario} step {index} reused an earlier request identity.");
                identifiers.Add(original, id);
            }
        }
    }

    private static State Observe(FlyScanWorkflow flow)
    {
        var origins = new Dictionary<int, DateTimeOffset>();
        var pulses = new List<Pulse>();
        foreach (var entry in flow.Events)
        {
            if (entry.Pulse is not { } pulse) continue;
            origins.TryAdd(entry.Attempt, pulse.On);
            pulses.Add(new(entry.Group, entry.Attempt, pulse.Name, pulse.Position,
                (pulse.On - origins[entry.Attempt]).Ticks, (pulse.Off - pulse.On).Ticks));
        }
        return new(flow.Workpiece, flow.Phase, flow.Interruption, flow.Attempt, flow.ConfirmedGroups,
            flow.LastConfirmedTrigger, flow.Counts, flow.CleanupPending, flow.TriggerArmed, flow.Feedback,
            flow.Outputs, pulses.ToArray());
    }

    private static void RequireRejectedRecovery(FlyScanWorkflow flow, FlyScanInputs inputs)
    {
        try { flow.Recover(inputs); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Recovery succeeded despite the recorded cleanup failure.");
    }

    private static Recording Read(string path)
    {
        var recording = JsonSerializer.Deserialize<Recording>(File.ReadAllText(path), JsonOptions);
        return recording is { Version: FormatVersion, Steps.Length: > 0 }
            ? recording
            : throw new InvalidDataException($"Unsupported or empty input recording: {path}");
    }

    private static void Write(string path, Recording recording) =>
        File.WriteAllText(path, JsonSerializer.Serialize(recording, JsonOptions));
}

using System.Diagnostics;
using System.Text.Json;
using Company.FlyScan.Runtime;

internal static class ProcessRestartChecks
{
    private const string PhaseMarker = "PROCESS_PHASE ";
    private record Pulse(int Group, int Attempt, TriggerPulse Trigger);
    private record Phase(string Name, int ProcessId, Guid? Workpiece, string State, string? Reason, int Attempt,
        int ConfirmedGroups, int LastConfirmedTrigger, ScanCounts Counts, bool OutputsActive, Pulse[] Pulses, Guid[] History);
    private record Run(int ProcessId, int ExitCode, bool KilledAtCheckpoint, string Log, Phase[] Phases);

    public static async Task VerifyAsync(string evidence, string replayDirectory, FlyScanRecipe recipe, Action<bool, string> check)
    {
        var directory = Path.Combine(evidence, "process-restart");
        Directory.CreateDirectory(directory);
        var checkpointPath = Path.Combine(directory, "workpiece.json");
        var interrupted = await RunAsync(directory, checkpointPath, "--interrupt-process", killAtCheckpoint: true);
        check(interrupted.KilledAtCheckpoint && interrupted.ExitCode != 0 &&
            interrupted.Phases is [{ Name: "checkpoint", State: nameof(ScanPhase.Scanning) }],
            "the controller process was terminated during scanning after acknowledging the checkpoint");
        var saved = interrupted.Phases.Single();
        var initialCounts = new ScanCounts(0, 1, recipe.Groups[0].Triggers.Count + 1);
        check(saved.ProcessId == interrupted.ProcessId && saved.Workpiece is not null && saved.Attempt == 1 &&
            saved.Counts == initialCounts && saved.ConfirmedGroups == 1 && saved.LastConfirmedTrigger == 1 &&
            saved.History.Length == 0, "the terminated process saved one group and the first trigger of group two");
        File.Copy(checkpointPath, Path.Combine(directory, "before-restart.json"), overwrite: true);

        var recovered = await RunAsync(directory, checkpointPath, "--recover-process");
        check(recovered.ExitCode == 0 && recovered.Phases is [{ Name: "restored" }, { Name: "completed" }],
            "a fresh controller process restored and then completed the interrupted workpiece");
        var restored = recovered.Phases[0];
        var completed = recovered.Phases[1];
        check(restored.ProcessId == recovered.ProcessId && restored.Workpiece == saved.Workpiece &&
            restored.State == nameof(ScanPhase.Interrupted) && restored.Reason == nameof(ScanInterruption.Restarted) &&
            restored.Attempt == saved.Attempt && restored.Counts == initialCounts &&
            restored.ConfirmedGroups == 1 && restored.LastConfirmedTrigger == 1 &&
            !restored.OutputsActive && restored.Pulses.Length == 0 && restored.History.Length == 0,
            "healthy inputs after process restart cannot auto-resume or emit triggers");

        var expectedTriggers = recipe.Groups[1].Triggers.Select(item => item.Name);
        int expectedPulseCount = initialCounts.Triggers + recipe.Groups[1].Triggers.Count;
        check(completed.ProcessId == recovered.ProcessId && completed.State == nameof(ScanPhase.Completed) &&
            completed.Workpiece == saved.Workpiece && completed.Attempt == 2 && completed.ConfirmedGroups == 2 &&
            completed.Counts == new ScanCounts(1, 2, expectedPulseCount) && !completed.OutputsActive &&
            completed.Pulses.All(pulse => pulse is { Group: 2, Attempt: 2 }) &&
            completed.Pulses.Select(pulse => pulse.Trigger.Name).SequenceEqual(expectedTriggers) &&
            completed.History is [var historyId] && historyId == saved.Workpiece,
            "explicit recovery replays only the unfinished group and records one completion in history");
        ScanInputRecording.Join(checkpointPath + ".before-inputs.json", checkpointPath + ".after-inputs.json",
            Path.Combine(replayDirectory, "process-restart.trace.json"));
        File.WriteAllText(Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(new { Status = "passed", Interrupted = interrupted, Recovered = recovered },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WritePhase(string name, FlyScanWorkflow flow, FlyScanHistory history)
    {
        var outputs = flow.Outputs;
        bool outputsActive = flow.TriggerArmed || outputs.VisionTrigger || outputs.HoldRobot ||
            outputs.RequestLoad is not null || outputs.RequestRobot is not null || outputs.RequestUnload is not null;
        var pulses = flow.Events.Where(entry => entry.Pulse is not null)
            .Select(entry => new Pulse(entry.Group, entry.Attempt, entry.Pulse!)).ToArray();
        var pieces = history.Pieces(history.Now.AddDays(-1), history.Now.AddTicks(1)).Select(piece => piece.Id).ToArray();
        var phase = new Phase(name, Environment.ProcessId, flow.Workpiece, flow.Phase.ToString(),
            flow.Interruption?.ToString(), flow.Attempt, flow.ConfirmedGroups, flow.LastConfirmedTrigger, flow.Counts,
            outputsActive, pulses, pieces);
        Console.WriteLine(PhaseMarker + JsonSerializer.Serialize(phase));
    }

    private static async Task<Run> RunAsync(string directory, string checkpointPath, string command, bool killAtCheckpoint = false)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the smoke test host.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProcessRestartChecks).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(checkpointPath);
        if (killAtCheckpoint)
            start.ArgumentList.Add("--wait-for-termination");

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot launch {command}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = new List<string>();
        var phases = new List<Phase>();
        bool killed = false;
        var log = command[2..] + ".log";
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                stdout.Add(line);
                if (!line.StartsWith(PhaseMarker, StringComparison.Ordinal))
                    continue;
                var phase = JsonSerializer.Deserialize<Phase>(line[PhaseMarker.Length..])
                    ?? throw new InvalidDataException($"Missing process evidence from {command}.");
                phases.Add(phase);
                if (killAtCheckpoint && phase.Name == "checkpoint" && !killed)
                {
                    process.Kill();
                    killed = true;
                }
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(directory, log),
                string.Join(Environment.NewLine, stdout) + Environment.NewLine + await stderr);
        }
        return new Run(process.Id, process.ExitCode, killed, log, phases.ToArray());
    }
}

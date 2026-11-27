using Company.FlyScan.Runtime;
using Company.Platform.Shared.Login;
using Microsoft.Data.Sqlite;

internal static class HistoryChecks
{
    internal static void Verify(string evidence, string profile, FlyScanRecipe recipe, Action<bool, string> check)
    {
        var path = Path.Combine(evidence, "g4-history.sqlite");
        var beginning = new DateTimeOffset(2026, 11, 24, 19, 59, 40, TimeSpan.FromHours(8));
        var clock = new TestClock(beginning);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        using (var history = new FlyScanHistory(path, clock))
        {
            Observe(0, true, false);
            Observe(2, true, true);
            var live = history.Alarms(beginning, beginning.AddSeconds(2).AddTicks(1));
            check(live.Online && live.Alarms is [{ Code: "CARD", End: null, Uncertain: false }],
                "D1 active alarm has the actual observed transition");
            Observe(5, false, true);
            check(!history.Online && history.LastConfirmed == beginning.AddSeconds(2), "D1 disconnect has unknown live feedback");
            Observe(9, true, true);
            Observe(11, true, false);
            Observe(12, true, false, true);
            Observe(13, true, false);
            var all = history.Alarms(beginning, beginning.AddSeconds(14));
            var alarm = all.Alarms.Single(item => item.Code == "CARD");
            check(alarm.Start == beginning.AddSeconds(2) && alarm.End == beginning.AddSeconds(11) &&
                alarm.Uncertain && alarm.KnownDuration(beginning, beginning.AddSeconds(14)) == TimeSpan.FromSeconds(5),
                "D1 active alarm spanning disconnect retains two confirmed intervals and explicit uncertainty");
            check(all.Gaps is [{ From: var gapFrom, To: var gapTo }] &&
                gapFrom == beginning.AddSeconds(5) && gapTo == beginning.AddSeconds(9),
                "D1 records the precise disconnect gap");
            check(alarm.KnownDuration(beginning.AddSeconds(3), beginning.AddSeconds(10)) == TimeSpan.FromSeconds(3),
                "D1 analysis clips both known intervals to the selected dates");
            check(history.Alarms(beginning.AddSeconds(11), beginning.AddSeconds(12)).Alarms.Count == 0 &&
                all.Alarms.Single(item => item.Code == "ESTOP").KnownDuration(beginning, beginning.AddSeconds(14)) == TimeSpan.FromSeconds(1),
                "D1 half-open filter excludes touching boundaries and preserves known emergency duration");

            var evening = new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(8));
            check(history.RecordCompletion(ids[0], evening.AddSeconds(-1), "设备工程师") &&
                history.RecordCompletion(ids[1], evening, "设备工程师") &&
                history.RecordCompletion(ids[2], evening.AddHours(12).AddSeconds(-1), "设备工程师") &&
                history.RecordCompletion(ids[3], evening.AddHours(12), "设备工程师"),
                "D1 seeds four independent shift-boundary completions");
            check(!history.RecordCompletion(ids[0], evening.AddDays(1), "管理员"), "D1 duplicate completion cannot move or recount the same piece");

            void Observe(int second, bool connected, bool alarm, bool emergency = false)
            {
                clock.Utc = beginning.AddSeconds(second);
                history.Observe(connected, alarm, emergency);
            }
        }

        clock.Utc = beginning.AddSeconds(17);
        using (var history = new FlyScanHistory(path, clock))
        {
            check(!history.Online, "D1 process restart does not show persisted status as live");
            history.Observe(true, alarm: false, emergency: false);
            var afterRestart = history.Alarms(beginning, beginning.AddSeconds(18));
            check(afterRestart.Gaps.Count == 2 && afterRestart.Gaps[^1].To == beginning.AddSeconds(17),
                "D1 reboot adds an observation gap without inventing alarm duration");
            var date = new DateOnly(2026, 11, 24);
            var days = FlyProductionCalendar.Read(history, date, date.AddDays(1));
            check(days[0].DayPieces == 1 && days[0].NightPieces == 2 && days[0].Total == 3 &&
                days[1].DayPieces == 1 && days[1].NightPieces == 0 && days.Sum(day => day.Total) == 4,
                "D1 daily and trend group confirmation at 20:00 and 08:00 into the correct local shift");
            check(days[0].Hours[11].Pieces == 1 && days[0].Hours[12].Pieces == 1 &&
                days[0].Hours[23].Pieces == 1 && days[1].Hours[0].Pieces == 1,
                "D1 hourly production matches independently seeded timestamps");
            check(history.Operations(beginning, beginning.AddDays(2), "设备", "工件完成").Count == 4 &&
                history.Operations(beginning, beginning.AddDays(2), "管理员", "工件完成").Count == 0,
                "D1 operation journal records only the successful deduplicated completions");
        }

        VerifyWorkflowRoundTrip();
        VerifyHeartbeatRuns();
        VerifyLiveAlarmProjection();
        VerifyObservationWriteFailures();
        VerifyCheckpointWriteFailures();
        VerifyCompletionWriteRetry();

        void VerifyCheckpointWriteFailures()
        {
            // Deny replacement of run.json, leaving the last committed checkpoint readable.
            foreach (bool atUnload in new[] { false, true })
            {
                string prefix = atUnload ? "checkpoint-unload" : "checkpoint-compare";
                string checkpoint = Path.Combine(evidence, prefix + ".json");
                using var history = new FlyScanHistory(Path.Combine(evidence, prefix + ".sqlite"), clock);
                using var machine = NewMachine();
                using var workflow = new FlyScanWorkflow(machine, checkpoint, history: history);
                workflow.Start(recipe, new());
                AdvanceUntil(workflow, () => workflow.Phase == (atUnload ? ScanPhase.AwaitUnload : ScanPhase.Scanning));
                Guid id = workflow.Workpiece!.Value;
                var counts = workflow.Counts;
                byte[] before = File.ReadAllBytes(checkpoint);
                var unload = workflow.Outputs.RequestUnload;
                using (File.Open(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var outputs = workflow.Outputs;
                    var robot = outputs.RequestRobot;
                    workflow.Tick(TimeSpan.FromMilliseconds(30), new(Robot: robot is null ? null : new(robot.Command, robot.Pose, true, false),
                        VisionReady: true, UnloadConfirmation: unload));
                    check(workflow.Phase == ScanPhase.Interrupted && workflow.Interruption == ScanInterruption.JournalFailed &&
                        workflow.Workpiece == id && workflow.Counts == counts && !workflow.TriggerArmed &&
                        workflow.Outputs == new FlyScanOutputs(null, null, false, null, false),
                        $"{prefix}: checkpoint failure withdraws outputs before confirming the pending event");
                    check(File.ReadAllBytes(checkpoint).SequenceEqual(before) &&
                        history.Pieces(clock.Utc.AddDays(-1), clock.Utc.AddDays(1)).Count == 0,
                        $"{prefix}: preserves the last durable checkpoint and records no completion");
                }
                workflow.Tick(TimeSpan.FromMilliseconds(30), new(VisionReady: true, UnloadConfirmation: unload));
                check(workflow.Phase == ScanPhase.Interrupted && workflow.Counts == counts,
                    $"{prefix}: repairing the file does not automatically recover the motion");
                workflow.Recover(new());
                AdvanceUntil(workflow, () => workflow.Phase == ScanPhase.Completed);
                check(workflow.Workpiece == id && workflow.Counts == new ScanCounts(1, 2, 5) &&
                    history.Pieces(clock.Utc.AddDays(-1), clock.Utc.AddDays(1)) is [{ Id: var saved }] && saved == id &&
                    (!atUnload || workflow.Events.All(entry => entry.Attempt != 2 || entry.Pulse is null)),
                    $"{prefix}: explicit recovery settles the same piece once and reruns only unfinished groups");
            }
        }

        void VerifyCompletionWriteRetry()
        {
            var workflowPath = Path.Combine(evidence, "completion-retry.json");
            var database = Path.Combine(evidence, "completion-retry.sqlite");
            var completedAt = new DateTimeOffset(2026, 11, 24, 19, 59, 59, TimeSpan.FromHours(8));
            clock.Utc = completedAt;
            Guid id;
            using (var history = new FlyScanHistory(database, clock))
            using (var machine = NewMachine())
            using (var workflow = new FlyScanWorkflow(machine, workflowPath, history: history))
            {
                using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fixture_completion_failure BEFORE INSERT ON operations WHEN NEW.action='工件完成'
                    BEGIN
                        SELECT RAISE(ABORT,'fixture completion operation failed');
                    END;
                    """;
                command.ExecuteNonQuery();
                workflow.Start(recipe, new());
                id = workflow.Workpiece!.Value;
                AdvanceUntil(workflow, () => workflow.Phase == ScanPhase.Completed);
                DateTimeOffset from = completedAt.AddHours(-1), to = completedAt.AddHours(1);
                check(workflow.Counts == new ScanCounts(1, 2, 5) && workflow.HistoryError is not null &&
                    history.Pieces(from, to).Count == 0 && history.Operations(from, to, "", "工件完成").Count == 0,
                    "D1 rejected history transaction leaves one durable completion and rolls back both SQLite rows");
                byte[] before = File.ReadAllBytes(workflowPath);
                bool nextRejected = false;
                try { workflow.Start(recipe, new()); }
                catch (InvalidOperationException) { nextRejected = true; }
                check(nextRejected && workflow.Workpiece == id && File.ReadAllBytes(workflowPath).SequenceEqual(before),
                    "D1 a new workpiece cannot replace the durable completion while its history write fails");

                command.CommandText = "DROP TRIGGER fixture_completion_failure;";
                command.ExecuteNonQuery();
                clock.Utc += TimeSpan.FromMilliseconds(500);
                workflow.Tick(TimeSpan.Zero, new());
                check(history.Pieces(from, to).Count == 0, "D1 idle history retries are throttled");
                clock.Utc += TimeSpan.FromSeconds(10);
                workflow.Tick(TimeSpan.Zero, new());
                check(workflow.HistoryError is null && workflow.Phase == ScanPhase.Completed && workflow.Workpiece == id &&
                    workflow.Counts == new ScanCounts(1, 2, 5) && File.ReadAllBytes(workflowPath).SequenceEqual(before) &&
                    history.Pieces(from, to) is [{ Id: var savedId, Time: var savedTime }] &&
                    savedId == id && savedTime == completedAt &&
                    history.Operations(from, to, "", "工件完成") is [{ Time: var logTime }] && logTime == completedAt,
                    "D1 idle retry restores both history rows once at the original confirmation time without starting motion");
                var date = FlyProductionCalendar.DayOf(completedAt);
                var day = FlyProductionCalendar.Read(history, date, date).Single();
                check(day.DayPieces == 1 && day.NightPieces == 0,
                    "D1 a retry after the 20:00 boundary preserves the original day-shift count");
            }
            using (var history = new FlyScanHistory(database, clock))
            using (var machine = NewMachine())
            using (var workflow = new FlyScanWorkflow(machine, workflowPath, history: history))
            {
                check(history.Pieces(completedAt.AddHours(-1), clock.Utc) is [{ Id: var savedId }] && savedId == id &&
                    history.Operations(completedAt.AddHours(-1), clock.Utc, "", "工件完成").Count == 1 &&
                    workflow.Counts == new ScanCounts(1, 2, 5), "D1 restart does not duplicate the repaired completion");
            }
        }

        void VerifyHeartbeatRuns()
        {
            using var history = new FlyScanHistory(Path.Combine(evidence, "g4-heartbeat.sqlite"), clock);
            clock.Utc = beginning;
            history.Observe(true, alarm: false, emergency: false);
            for (int second = 1; second <= 12; second++)
            {
                clock.Utc = beginning.AddSeconds(second);
                history.Observe(true, alarm: true, emergency: false);
            }
            clock.Utc = beginning.AddSeconds(13);
            history.Observe(true, alarm: false, emergency: false);
            var report = history.Alarms(beginning, beginning.AddSeconds(14));
            check(report.Gaps.Count == 0 && report.Alarms.Single().KnownDuration(beginning, beginning.AddSeconds(14)) ==
                TimeSpan.FromSeconds(12), "D1 stable heartbeat extends a known interval across more than five seconds");
            clock.Utc = beginning.AddSeconds(20);
            check(!history.Online && history.Alarms(beginning, clock.Utc).Gaps.Count == 1,
                "D1 stale feedback becomes an unknown interval before the next observation");
        }

        void VerifyLiveAlarmProjection()
        {
            foreach (bool withReset in new[] { false, true })
            {
                string path = Path.Combine(evidence, $"live-alarm-reset-{withReset}.sqlite");
                clock.Utc = beginning;
                using (var history = new FlyScanHistory(path, clock))
                {
                    if (withReset) history.Observe(true, false, false);
                    history.Observe(true, true, true);
                    VerifyLiveMatchesHistory(history, "simultaneous alarms following a same-timestamp sample");
                    var first = history.LiveAlarms(history.Now);
                    for (int second = 1; second <= 7; second++)
                    {
                        clock.Utc = beginning.AddSeconds(second);
                        history.Observe(true, true, true);
                        VerifyLiveMatchesHistory(history, "extended heartbeat");
                    }
                    check(first.Alarms.All(alarm => alarm.KnownSpans.Count == 0) &&
                        history.LiveAlarms(history.Now).Alarms.All(alarm =>
                            alarm.KnownDuration(beginning, history.Now) == TimeSpan.FromSeconds(7)),
                        "Live reads preserve earlier snapshots while tracking committed heartbeat extensions");
                    clock.Utc = beginning.AddSeconds(13);
                    VerifyLiveMatchesHistory(history, "stale feedback");
                    clock.Utc = beginning.AddSeconds(7);
                    VerifyLiveMatchesHistory(history, "available feedback after a temporary stale read");
                    clock.Utc = beginning.AddSeconds(8);
                    history.Observe(true, true, false);
                    VerifyLiveMatchesHistory(history, "one of two alarms clears");
                    clock.Utc = beginning.AddSeconds(9);
                    history.Observe(false, false, false);
                    VerifyLiveMatchesHistory(history, "disconnection with an open alarm");
                }

                clock.Utc = beginning.AddSeconds(11);
                using var reopened = new FlyScanHistory(path, clock);
                VerifyLiveMatchesHistory(reopened, "reopened history with an earlier open alarm");
                reopened.Observe(true, true, true);
                VerifyLiveMatchesHistory(reopened, "first sample of the new session");
                reopened.Observe(true, false, false);
                VerifyLiveMatchesHistory(reopened, "all-clear at the same timestamp");
            }
        }

        void VerifyLiveMatchesHistory(FlyScanHistory history, string stage)
        {
            var now = history.Now;
            var full = history.Alarms(DateTimeOffset.MinValue, now.AddTicks(1));
            var expected = full.Alarms.Where(row => row.End is null).OrderByDescending(row => row.Start)
                .ThenBy(row => row.Code, StringComparer.Ordinal).ToArray();
            var live = history.LiveAlarms(now);
            check(live.Online == full.Online && live.Alarms.Count == expected.Length &&
                live.Alarms.Zip(expected).All(pair =>
                    (pair.First with { KnownSpans = pair.Second.KnownSpans }) == pair.Second &&
                    pair.First.KnownSpans.SequenceEqual(pair.Second.KnownSpans) &&
                    pair.First.KnownDuration(beginning.AddSeconds(2), now) ==
                        pair.Second.KnownDuration(beginning.AddSeconds(2), now)),
                $"Live alarm fields and clipped observation intervals match history after {stage}");
        }

        void VerifyObservationWriteFailures()
        {
            foreach (bool transition in new[] { true, false })
            {
                string name = transition ? "alarm-transition-write-failure" : "alarm-heartbeat-write-failure";
                var database = Path.Combine(evidence, name + ".sqlite");
                var lastConfirmed = beginning.AddSeconds(2);
                var resumed = beginning.AddSeconds(4);
                var finished = beginning.AddSeconds(6);
                using (var history = new FlyScanHistory(database, clock))
                {
                    clock.Utc = beginning;
                    history.Observe(true, alarm: false, emergency: false);
                    clock.Utc = beginning.AddSeconds(1);
                    history.Observe(true, alarm: true, emergency: false);
                    clock.Utc = lastConfirmed;
                    history.Observe(true, alarm: true, emergency: false);

                    using var fixture = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                    fixture.Open();
                    using var command = fixture.CreateCommand();
                    string operation = transition ? "INSERT" : "UPDATE";
                    command.CommandText = $"""
                        CREATE TRIGGER fixture_observation_failure BEFORE {operation} ON observations
                        BEGIN
                            SELECT RAISE(ABORT,'fixture observation write failed');
                        END;
                        """;
                    command.ExecuteNonQuery();
                    clock.Utc = beginning.AddSeconds(3);
                    bool rejected = false;
                    try { history.Observe(connected: !transition, alarm: true, emergency: false); }
                    catch (SqliteException error) when (error.Message.Contains("fixture observation write failed", StringComparison.Ordinal))
                    { rejected = true; }
                    check(rejected, $"{name}: SQLite rejects the intended write");

                    var fault = history.Alarms(beginning, clock.Utc);
                    check(!fault.Online && history.LastConfirmed == lastConfirmed &&
                        fault.Gaps.SequenceEqual([new ObservationSpan(lastConfirmed, clock.Utc)]) &&
                        fault.Alarms is [{ Code: "CARD", Uncertain: true }],
                        $"{name}: a rejected write immediately marks live status and the interval as unknown");
                    VerifyLiveMatchesHistory(history, name + " rejected write");
                    command.CommandText = "DROP TRIGGER fixture_observation_failure";
                    command.ExecuteNonQuery();
                    clock.Utc = resumed;
                    history.Observe(true, alarm: true, emergency: false);
                    check(history.Online && history.LastConfirmed == resumed,
                        $"{name}: the next persisted sample restores live feedback");
                    VerifyLiveMatchesHistory(history, name + " recovered write");
                    clock.Utc = beginning.AddSeconds(5);
                    history.Observe(true, alarm: true, emergency: false);
                    clock.Utc = finished;
                    history.Observe(true, alarm: false, emergency: false);
                    VerifyGap(history, "queries");
                }
                using var reopened = new FlyScanHistory(database, clock);
                VerifyGap(reopened, "queries after restart");

                void VerifyGap(FlyScanHistory history, string source)
                {
                    var report = history.Alarms(beginning, finished.AddTicks(1));
                    var alarm = report.Alarms.Single(item => item.Code == "CARD");
                    check(report.Gaps.SequenceEqual([new ObservationSpan(lastConfirmed, resumed)]) &&
                        alarm.Uncertain && alarm.End == finished &&
                        alarm.KnownSpans.SequenceEqual([new ObservationSpan(beginning.AddSeconds(1), lastConfirmed),
                            new ObservationSpan(resumed, finished)]) &&
                        alarm.KnownDuration(beginning, finished) == TimeSpan.FromSeconds(3),
                        $"{name}: {source} retain the two-second gap and three seconds of confirmed alarm time");
                }
            }
        }

        void VerifyWorkflowRoundTrip()
        {
            var workflowPath = Path.Combine(evidence, "g4-checkpoint.json");
            var integratedPath = Path.Combine(evidence, "g4-workflow.sqlite");
            clock.Utc = new DateTimeOffset(2026, 11, 24, 19, 59, 59, TimeSpan.FromHours(8));
            Guid id;
            using (var history = new FlyScanHistory(integratedPath, clock))
            using (var machine = NewMachine())
            using (var workflow = new FlyScanWorkflow(machine, workflowPath, history: history))
            {
                workflow.Start(recipe, new());
                id = workflow.Workpiece!.Value;
                AdvanceUntil(workflow, () => workflow.Phase == ScanPhase.Scanning && workflow.ConfirmedGroups == 1);
                workflow.Interrupt(ScanInterruption.Paused, new());
                check(history.Pieces(clock.Utc.AddDays(-1), clock.Utc.AddDays(1)).Count == 0,
                    "D1 interrupted attempt and first confirmed group do not count as finished work");
                workflow.Recover(new());
                clock.Utc = clock.Utc.AddSeconds(1);
                AdvanceUntil(workflow, () => workflow.Phase == ScanPhase.Completed);
            }
            using (var history = new FlyScanHistory(integratedPath, clock))
            using (var machine = NewMachine())
            using (var workflow = new FlyScanWorkflow(machine, workflowPath, history: history))
            {
                var completed = history.Pieces(clock.Utc.AddDays(-1), clock.Utc.AddDays(1));
                check(completed is [{ Id: var savedId, Time: var time }] && savedId == id && time == clock.Utc &&
                    workflow.Counts.Pieces == 1, "D1 workflow start crosses shift, retries, writes one completion, and reconciles safely on reopen");
                var actions = history.Operations(clock.Utc.AddDays(-1), clock.Utc.AddDays(1), "", "");
                check(actions.Count(row => row.Action == "工件完成") == 1 && actions.Any(row => row.Action == "中断") &&
                    actions.Any(row => row.Action == "恢复"), "D1 persists retry actions and exactly one completion after reopen");
            }
        }

        SimulatedMachine NewMachine()
        {
            var machine = new SimulatedMachine(profile);
            machine.SelectRole(UserRole.EquipmentEngineer);
            machine.SetFeedback(20, 0);
            return machine;
        }
    }

    private static void AdvanceUntil(FlyScanWorkflow workflow, Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++)
        {
            var output = workflow.Outputs;
            var feedback = output.RequestRobot is { } request ? new RobotPoseFeedback(request.Command, request.Pose, true, false) : null;
            workflow.Tick(TimeSpan.FromMilliseconds(12),
                new FlyScanInputs(output.RequestLoad, feedback, true, output.RequestUnload));
        }
        if (!done()) throw new InvalidOperationException(workflow.Message);
    }

    private sealed class TestClock(DateTimeOffset utc) : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}

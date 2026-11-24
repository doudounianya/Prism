using Company.FlyScan.Runtime;
using Company.Platform.Shared.Login;

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

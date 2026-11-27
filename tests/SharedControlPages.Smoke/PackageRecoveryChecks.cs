using Company.FlyScan.Runtime;
using Company.FlyScan.Shell;
using Company.Hmi.Presentation;
using Company.Platform.Shared.Login;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private const string RecoveryRecipe = "G6 恢复复跑配方";
    private const string FirstPose = "G6-REC-A";
    private const string SecondPose = "G6-REC-B";
    private const string ExportedRecipe = "operator-recipe.json";
    private static readonly JsonSerializerOptions CheckpointFormat = new(JsonSerializerDefaults.Web);

    private static bool CheckPackageRecoveryInputs(string phase, string folder, string journal)
    {
        var database = Path.Combine(folder, "history.sqlite");
        var recipe = Path.Combine(folder, ExportedRecipe);
        bool hasData = File.Exists(journal) && File.Exists(database) && File.Exists(recipe);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "package-manifest.json")) ||
            (phase == "checkpoint" ? File.Exists(journal) || File.Exists(database) || File.Exists(recipe) : !hasData))
        {
            Console.Error.WriteLine("Package recovery requires a verified installation and the matching isolated fixture data.");
            return false;
        }
        return true;
    }

    private static async Task VerifyPackageRecovery(Window window, string phase, string journal)
    {
        T Named<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var scan = Named<FlyScanPage>("ScanPage");
        T Scan<T>(string name) where T : FrameworkElement => (T)scan.FindName(name);
        var pages = Named<TabControl>("Pages");
        pages.SelectedItem = Named<TabItem>("FlyScanTab");
        await Wait(() => window.IsVisible && scan.IsLoaded && scan.IsVisible,
            "the packaged FlyScan desktop opens its scanning page", attempts: 240);
        var work = Scan<TabControl>("WorkTabs");
        var step = Scan<Button>("StepButton");
        var start = Scan<Button>("StartButton");
        var recover = Scan<Button>("RecoverButton");
        string exportPath = Path.Combine(Path.GetDirectoryName(journal)!, ExportedRecipe);
        Scan<CheckBox>("AutoStep").IsChecked = false;

        async Task Feedback(int mode, int status)
        {
            Named<ComboBox>("SimulationRole").SelectedItem = SimulatedMachine.Roles.Single(item => item.Role == UserRole.EquipmentEngineer);
            Named<ComboBox>("SimulationMode").SelectedItem = SimulatedMachine.Modes.Single(item => item.Code == mode);
            Named<ComboBox>("SimulationStatus").SelectedItem = SimulatedMachine.States.Single(item => item.Code == status);
            Click(window, "应用反馈");
            await Idle();
        }

        SavedRun Read() => JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(journal), CheckpointFormat)
            ?? throw new InvalidDataException("The packaged workflow did not save a checkpoint.");
        async Task Drive(Func<SavedRun, bool> until, string description)
        {
            for (int index = 0; index < 150 && !until(Read()) && step.IsEnabled; index++)
            {
                Click(scan, "单步");
                await Idle();
            }
            Check(until(Read()), description);
        }
        void CheckOutputsOff()
        {
            var signals = Scan<TextBlock>("Handshake").Text;
            Check(!step.IsEnabled && signals.Contains("触发使能 OFF", StringComparison.Ordinal) &&
                signals.Contains("触发 OFF", StringComparison.Ordinal) &&
                signals.Contains("保持请求 OFF", StringComparison.Ordinal) &&
                signals.Contains("上料请求 OFF", StringComparison.Ordinal) &&
                signals.Contains("下料请求 OFF", StringComparison.Ordinal) &&
                signals.Contains("机器人位号 —", StringComparison.Ordinal),
                "idle or unconfirmed recovery sends no load, robot, trigger or unload request");
        }

        if (phase == "checkpoint")
        {
            await Feedback(0, 30);
            var recipe = Scan<TextBox>("RecipeName");
            Check(!recipe.IsReadOnly, "manual debug permits editing the fixture recipe");
            recipe.Text = RecoveryRecipe;
            var group = Scan<ComboBox>("GroupSelector");
            Scan<TextBox>("RobotPose").Text = FirstPose;
            group.SelectedIndex = 1;
            await Idle();
            Scan<TextBox>("RobotPose").Text = SecondPose;
            group.SelectedIndex = 0;
            await Feedback(20, 0);
            work.SelectedIndex = 1;
            await Idle();

            Check(start.IsEnabled, "automatic stopped mode permits the first packaged scan");
            Click(scan, "开始双组扫描");
            await Wait(() => File.Exists(journal) && step.IsEnabled, "the packaged scan starts and saves its checkpoint");
            await Drive(saved => saved.Workpiece.Phase == ScanPhase.Completed && saved.Counts == new ScanCounts(1, 2, 5),
                "the packaged scan records a prior completed workpiece and five trigger pulses");
            var prior = Read().Workpiece.Id;

            await Wait(() => start.IsEnabled, "the next packaged workpiece can be started");
            Click(scan, "开始双组扫描");
            await Wait(() => Read().Workpiece.Id != prior && step.IsEnabled, "a distinct second workpiece starts");
            await Drive(saved => saved.Workpiece is { Phase: ScanPhase.Scanning, GroupsCompleted: 1, LastTrigger: 1 },
                "the second workpiece reaches the first confirmed trigger of its unfinished group");
            var checkpoint = Read();
            Check(checkpoint.Counts == new ScanCounts(1, 3, 9) &&
                checkpoint.Workpiece.Recipe is { Name: RecoveryRecipe, Groups: [{ RobotPose: FirstPose }, { RobotPose: SecondPose }] },
                "the persisted checkpoint contains the original recipe, the confirmed group and nine observed pulses");
            checkpoint.Workpiece.Recipe.Save(exportPath);
            Console.Out.Flush();
            // Simulate the isolated probe disappearing while the real packaged workflow is still running.
            Environment.Exit(0);
            return;
        }

        work.SelectedIndex = 1;
        await Feedback(20, 0);
        var saved = Read();
        Check(saved.Workpiece.Id != Guid.Empty && saved.Workpiece.Recipe.Name == RecoveryRecipe,
            "the restored installation reads the backed-up workpiece and custom recipe");
        var exported = FlyScanRecipe.Load(exportPath);
        Check(JsonSerializer.Serialize(exported, CheckpointFormat) == JsonSerializer.Serialize(saved.Workpiece.Recipe, CheckpointFormat),
            "the published recipe reader loads the separate backup file with the same robot poses, waypoints and triggers");
        CheckOutputsOff();
        if (phase is "resume" or "rollback-preview")
        {
            Check(saved.Workpiece is { Phase: ScanPhase.Interrupted, Reason: ScanInterruption.Restarted, GroupsCompleted: 1,
                    LastTrigger: 1, Attempt: 1 } && saved.Counts == new ScanCounts(1, 3, 9) &&
                recover.IsEnabled && !start.IsEnabled, "the package waits for explicit recovery while retaining the interrupted progress");
            work.SelectedIndex = 0;
            await Idle();
            if (phase == "rollback-preview")
            {
                bool matches = Scan<TextBox>("RecipeName").Text == RecoveryRecipe && Scan<TextBox>("RobotPose").Text == FirstPose;
                Scan<ComboBox>("GroupSelector").SelectedIndex = 1;
                await Idle();
                matches &= Scan<TextBox>("RobotPose").Text == SecondPose &&
                    Scan<DataGrid>("TriggerTable").Items.Cast<ScanTrigger>().Select(item => item.Name).SequenceEqual(["B1", "B2"]);
                Console.WriteLine("ROLLBACK_PREVIEW " + JsonSerializer.Serialize(new { matches }));
                return;
            }
            Check(Scan<TextBox>("RecipeName").Text == RecoveryRecipe && Scan<TextBox>("RobotPose").Text == FirstPose,
                "the restored teaching page shows the pending workpiece's recipe and first robot pose");
            Scan<ComboBox>("GroupSelector").SelectedIndex = 1;
            await Idle();
            Check(Scan<TextBox>("RobotPose").Text == SecondPose &&
                Scan<DataGrid>("TriggerTable").Items.Cast<ScanTrigger>().Select(item => item.Name).SequenceEqual(["B1", "B2"]),
                "the second restored group shows the saved robot pose and ordered trigger list");
            work.SelectedIndex = 1;
            await Idle();
            Click(scan, "确认恢复");
            await Wait(() => step.IsEnabled && Read().Workpiece.Attempt == 2, "operator confirmation starts one new attempt");
            await Drive(next => next.Workpiece.Phase == ScanPhase.Completed && next.Counts == new ScanCounts(2, 4, 11),
                "recovery repeats the unfinished group and counts the second workpiece once");
            var resumed = Read();
            Check(resumed.Workpiece.Id == saved.Workpiece.Id && resumed.Workpiece.Attempt == 2 &&
                Scan<DataGrid>("FlightEvents").Items.Cast<ScanFlightEvent>().Where(item => item.Pulse is not null)
                    .Reverse().Select(item => (item.Group, item.Attempt, item.Pulse!.Name))
                    .SequenceEqual([(2, 2, "B1"), (2, 2, "B2")]),
                "the same workpiece emits only the two ordered triggers in the unfinished group");
        }
        else
            Check(saved.Workpiece is { Phase: ScanPhase.Completed, Attempt: 2 } && saved.Counts == new ScanCounts(2, 4, 11) &&
                !recover.IsEnabled && Scan<DataGrid>("FlightEvents").Items.Count == 0,
                "a further restart preserves completion and emits no replayed events");

        pages.SelectedItem = Named<TabItem>("ProductionTab");
        await Idle();
        Click(Named<ProductionTodayView>("TodayPage"), "产量趋势");
        await Idle();
        var trend = Named<ProductionTrendView>("TrendPage");
        Check(trend.IsVisible && Walk<DataGrid>(trend).SelectMany(table => table.Items.Cast<object>()).Sum(row =>
            row.GetType().GetProperty("Total")?.GetValue(row) is int total ? total : 0) == 2,
            "the packaged production trend shows both completed workpieces");
    }

    private sealed record SavedRun(ScanCounts Counts, SavedPiece Workpiece);
    private sealed record SavedPiece(Guid Id, FlyScanRecipe Recipe, ScanPhase Phase, int GroupsCompleted,
        int Attempt, int LastTrigger, ScanInterruption? Reason);
}

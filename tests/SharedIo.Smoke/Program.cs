using Company.FlyScan.Runtime;
using Company.Hmi.Contracts;
using Company.Hmi.Presentation;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var journal = Path.Combine(Path.GetTempPath(), $"flyscan-io-{Guid.NewGuid():N}", "run.json");
        var app = new Company.FlyScan.Shell.App { SimulationJournalPath = journal };
        app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await VerifyAsync(app.MainWindow!, args.FirstOrDefault());
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        }, DispatcherPriority.ApplicationIdle);
        return app.Run();
    }

    private static async Task VerifyAsync(Window window, string? evidence)
    {
        var pages = (TabControl)window.FindName("Pages");
        var ioTab = (TabItem)window.FindName("IoTab");
        var scanTab = (TabItem)window.FindName("FlyScanTab");
        var scan = (FrameworkElement)window.FindName("ScanPage");
        var ready = (CheckBox)scan.FindName("ReadySignal");
        var connected = (CheckBox)scan.FindName("ConnectedSignal");
        pages.SelectedItem = ioTab;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var monitor = (SimulatedIoMonitor)window.DataContext;
        var view = Find<IoMonitorView>(window).Single();
        var selectors = Find<ComboBox>(view).ToArray();
        var rows = (DataGrid)view.FindName("Signals");
        await Idle();
        Check(selectors.Length == 3 && rows.Columns.Count == 4 && rows.IsReadOnly, "shared filters and read-only table");
        Check(ReferenceEquals(view.DataContext, monitor), "independent FlyScan shell binds the shared page");
        Check(rows.Items.Count == 16 && monitor.Items[2] is { DisplayName: "视觉 Ready", Value: true } &&
            monitor.Items[0] is { DisplayName: "上料确认", Value: false }, "primary DI shows the actual Ready and load feedback");
        Check(monitor.Items[15].Value is null && monitor.Items[15].StateText == "未配置 / 禁用", "unconfigured channel is separate");
        Check(Find<TextBlock>(view).Any(t => t.Text == "6 有效 / 10 未知"), "primary quality summary reflects configured signals");
        pages.SelectedItem = scanTab;
        await Idle();
        ready.IsChecked = false;
        ready.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ready));
        pages.SelectedItem = ioTab;
        await Idle();
        Check(monitor.Items[2].Value == false, "turning off workflow Ready updates the monitored input");
        Invoke(window, "模拟断开");
        await Idle();
        Check(connected.IsChecked == false && monitor.Items.All(item => item.Value is null) &&
            monitor.Items[2].LastValueText == "False", "primary toolbar disconnect also invalidates workflow IO, keeping last value separately");
        pages.SelectedItem = scanTab;
        await Idle();
        ready.IsChecked = true;
        ready.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ready));
        connected.IsChecked = true;
        connected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, connected));
        pages.SelectedItem = ioTab;
        await Idle();
        Check(monitor.Items[2].Value == true, "workflow reconnect reads the current Ready value in the same primary monitor");
        rows.SelectedIndex = 1;
        await Idle();
        Check(Find<TextBlock>(view).Any(t => t.Text == monitor.Items[1].BindingDetails), "selected row binding details");
        selectors[0].SelectedItem = "IMC-SIM-02";
        selectors[2].SelectedItem = "1";
        await Idle();
        Check(monitor.Items[5].Value is true && monitor.Items[4].Value is false, "device and group selections use the matching card");
        Check(Find<TextBlock>(view).Any(t => t.Text == "15 有效 / 1 未知"), "sample-card quality summary reflects its configured channels");
        selectors[1].SelectedItem = "DO";
        await Idle();
        Check(monitor.Items.Where(i => i.Value.HasValue).All(i => i.Value == false), "selecting DO does not alter outputs");
        selectors[1].SelectedItem = "DI";
        await Idle();
        if (evidence is not null) Save(window, evidence, "flyscan-connected");
        Invoke(window, "模拟断开");
        await Idle();
        Check(monitor.Items.All(i => i.Value is null) && monitor.Items[5].LastValueText == "True" && connected.IsChecked == true,
            "sample-card disconnect keeps its history without disconnecting the workcell");
        Check(Find<TextBlock>(view).Any(t => t.Text == "0 有效 / 16 未知"), "disconnected summary matches unknown values");
        if (evidence is not null) Save(window, evidence, "flyscan-disconnected");
        Invoke(window, "模拟重连");
        await Idle();
        Check(monitor.Items[5].Value is true && monitor.Items[0].Value is false, "reconnect reads a new simulation session");
        window.Hide();
        var previous = monitor.Items;
        monitor.SetConnection("IMC-SIM-02", connected: false);
        Check(ReferenceEquals(previous, monitor.Items), "hidden page does not refresh");
        window.Show();
        await Idle();
        Check(monitor.Items.All(i => i.Value is null), "reshowing reads the current connection state");
        VerifySharedFixture(monitor);
        Console.WriteLine("Shared IO smoke passed.");
    }

    private static void VerifySharedFixture(SimulatedIoMonitor monitor)
    {
        var (fixture, hash) = SharedIoFixture.Load();
        Console.WriteLine($"Shared IO fixture SHA-256 {hash}");
        monitor.SelectedDevice = "IMC-SIM-01";
        monitor.SelectedGroup = "0";
        monitor.SelectedSignalType = "DI";

        var transfer = new TransferRequest(Guid.NewGuid(), Guid.NewGuid());
        var robot = new RobotPoseRequest(Guid.NewGuid(), "fixture");
        FlyScanInputs Inputs(bool[] values) => new(
            LoadConfirmation: values[0] ? transfer : null,
            Robot: values[1] ? new RobotPoseFeedback(robot.Command, robot.Pose, InPosition: true, Moving: false) : null,
            VisionReady: values[2], UnloadConfirmation: values[3] ? transfer : null, Alarm: values[4], EmergencyStop: values[5]);
        FlyScanOutputs Outputs(bool[] values) => new(values[0] ? transfer : null, values[1] ? robot : null,
            HoldRobot: values[2], RequestUnload: values[4] ? transfer : null, VisionTrigger: values[3]);

        long originalEpoch = monitor.ScanConnectionEpoch;
        var originalInputs = Inputs(fixture.DiBefore);
        var originalOutputs = Outputs(fixture.DoBefore);
        monitor.PublishScanIo(originalEpoch, originalInputs, originalOutputs);
        Check(SharedIoFixture.Matches(monitor, "DI", fixture.DiBefore),
            "shared fixture matches all initial DI rows, quality, timestamps and unconfigured channels");
        monitor.SelectedSignalType = "DO";
        Check(SharedIoFixture.Matches(monitor, "DO", fixture.DoBefore),
            "shared fixture matches all initial DO rows, quality, timestamps and unconfigured channels");
        monitor.SelectedSignalType = "DI";

        monitor.SetConnection("IMC-SIM-01", connected: false);
        Check(monitor.Items.All(row => row.Value is null) && monitor.Items[0].LastValueText == "True" &&
            monitor.Items[1].LastValueText == "False" && monitor.Items[15].StateText == "未配置 / 禁用",
            "disconnect preserves historical DI and keeps unconfigured channels separate");
        monitor.SetConnection("IMC-SIM-01", connected: true);
        long currentEpoch = monitor.ScanConnectionEpoch;
        Check(currentEpoch > originalEpoch, "reconnecting the scan card advances the session epoch");
        monitor.PublishScanIo(currentEpoch, Inputs(fixture.DiAfter), Outputs(fixture.DoAfter));
        Check(SharedIoFixture.Matches(monitor, "DI", fixture.DiAfter), "shared fixture matches all reconnected DI rows");
        monitor.SelectedSignalType = "DO";
        Check(SharedIoFixture.Matches(monitor, "DO", fixture.DoAfter), "shared fixture matches all reconnected DO rows");

        var currentRows = monitor.Items;
        monitor.PublishScanIo(originalEpoch, originalInputs, originalOutputs);
        Check(ReferenceEquals(currentRows, monitor.Items) && SharedIoFixture.Matches(monitor, "DO", fixture.DoAfter),
            "late feedback from the previous scan-card session cannot change current DO");
        monitor.SelectedSignalType = "DI";
        Check(SharedIoFixture.Matches(monitor, "DI", fixture.DiAfter), "late feedback cannot replace the new DI values or history");
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Check(bool ok, string check)
    {
        if (!ok) throw new InvalidOperationException(check);
        Console.WriteLine($"PASS {check}");
    }

    private static void Invoke(Window root, string name)
    {
        var diagnostics = (Expander)root.FindName("SimulationDiagnostics");
        diagnostics.IsExpanded = true;
        root.UpdateLayout();
        var button = Find<Button>(diagnostics).Single(b => b.IsVisible && Equals(b.Content, name));
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Save(Window window, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        ((Expander)window.FindName("SimulationDiagnostics")).IsExpanded = false;
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}

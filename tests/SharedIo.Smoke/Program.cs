using Company.FlyScan.Runtime;
using Company.Hmi.Contracts;
using Company.Hmi.Presentation;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
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
        ((TabControl)window.FindName("Pages")).SelectedItem = window.FindName("IoTab");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var monitor = (SimulatedIoMonitor)window.DataContext;
        var view = Find<IoMonitorView>(window).Single();
        var selectors = Find<ComboBox>(view).ToArray();
        var rows = (DataGrid)view.FindName("Signals");
        await Idle();
        Check(selectors.Length == 3 && rows.Columns.Count == 4 && rows.IsReadOnly, "shared filters and read-only table");
        Check(ReferenceEquals(view.DataContext, monitor), "independent FlyScan shell binds the shared page");
        Check(rows.Items.Count == 16 && monitor.Items[0].Value is true && monitor.Items[1].Value is false, "DI has live ON and OFF");
        Check(monitor.Items[15].Value is null && monitor.Items[15].StateText == "未配置 / 禁用", "unconfigured channel is separate");
        Check(Find<TextBlock>(view).Any(t => t.Text == "15 有效 / 1 未知"), "quality summary binds");
        rows.SelectedIndex = 1;
        await Idle();
        Check(Find<TextBlock>(view).Any(t => t.Text == monitor.Items[1].BindingDetails), "selected row binding details");
        selectors[0].SelectedItem = "IMC-SIM-02";
        selectors[2].SelectedItem = "1";
        await Idle();
        Check(monitor.Items[5].Value is true && monitor.Items[4].Value is false, "device and group selections use the matching card");
        selectors[1].SelectedItem = "DO";
        await Idle();
        Check(monitor.Items.Where(i => i.Value.HasValue).All(i => i.Value == false), "selecting DO does not alter outputs");
        selectors[1].SelectedItem = "DI";
        await Idle();
        if (evidence is not null) Save(window, evidence, "flyscan-connected");
        Invoke(window, "模拟断开");
        await Idle();
        Check(monitor.Items.All(i => i.Value is null) && monitor.Items[5].LastValueText == "True", "disconnect invalidates live values and retains separate history");
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
        Console.WriteLine("Shared IO smoke passed.");
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Check(bool ok, string check)
    {
        if (!ok) throw new InvalidOperationException(check);
        Console.WriteLine($"PASS {check}");
    }

    private static void Invoke(Window root, string name)
    {
        var button = Find<Button>(root).Single(b => Equals(b.Content, name));
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
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}

using Company.FlyScan.Runtime;
using Prism.Ioc;
using System.Windows;
using System.IO;

namespace Company.FlyScan.Shell;

public partial class App : PrismApplication
{
    public string SimulationJournalPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Company", "FlyScan", "Simulation", "run.json");

    protected override void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<SimulatedIoMonitor>();
        registry.RegisterInstance(new SimulatedMachine(Path.Combine(AppContext.BaseDirectory, "Config", "FlyScan.13Axes.json")));
        registry.RegisterSingleton<SimulatedModePage>();
        registry.RegisterSingleton<SimulatedAxisPage>();
        registry.RegisterSingleton<SimulatedCylinderPage>();
        registry.RegisterInstance(new FlyScanStorage(SimulationJournalPath));
        registry.RegisterInstance(new FlyScanHistory(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(SimulationJournalPath))!, "history.sqlite")));
    }

    protected override Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void OnExit(ExitEventArgs e)
    {
        Container.Resolve<SimulatedMachine>().Dispose();
        Container.Resolve<FlyScanHistory>().Dispose();
        base.OnExit(e);
    }
}

public sealed record FlyScanStorage(string JournalPath);

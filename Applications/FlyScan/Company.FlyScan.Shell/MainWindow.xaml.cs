using Company.FlyScan.Runtime;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Company.FlyScan.Shell;

public partial class MainWindow : Window
{
    private readonly SimulatedIoMonitor _monitor;
    private readonly SimulatedMachine _machine;
    private readonly string? _historyLocation;
    private readonly DispatcherTimer _axisRefresh = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _summaryRefresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(SimulatedIoMonitor monitor, SimulatedMachine machine, SimulatedModePage mode,
        SimulatedAxisPage axis, SimulatedCylinderPage cylinder, FlyScanStorage storage, FlyScanHistory history)
    {
        _monitor = monitor;
        _machine = machine;
        _historyLocation = Path.GetDirectoryName(storage.JournalPath);
        InitializeComponent();
        ParameterPages.SelectedIndex = AlarmPages.SelectedIndex = ProductionPages.SelectedIndex = 0;
        DataContext = _monitor;
        IoPage.DataContext = monitor;
        ModePage.DataContext = mode;
        AxisPage.DataContext = axis;
        CylinderPage.DataContext = cylinder;
        SimulationRole.ItemsSource = SimulatedMachine.Roles;
        SimulationMode.ItemsSource = SimulatedMachine.Modes;
        SimulationStatus.ItemsSource = SimulatedMachine.States;
        SimulationRole.SelectedIndex = SimulationMode.SelectedIndex = SimulationStatus.SelectedIndex = 0;
        SetupHistoryPages(history);
        FlySettings.DataContext = new SettingsDisplay(_historyLocation, machine.Actor);
        ScanPage.Initialize(machine, storage.JournalPath, history);
        ScanPage.AxisPageRequested += (_, _) => NavigateParameters("AxisParameterView");
        AxisPage.ParameterPageRequested += NavigateParameters;
        CylinderPage.ParameterPageRequested += NavigateParameters;
        _axisRefresh.Tick += async (_, _) => await axis.RefreshAsync();
        _axisRefresh.Start();
        Closed += (_, _) => { _axisRefresh.Stop(); _summaryRefresh.Stop(); ScanPage.Dispose(); machine.Dispose(); monitor.Dispose(); };
    }

    private void NavigateParameters(string target)
    {
        Pages.SelectedItem = ParametersTab;
        ParameterPages.SelectedItem = target == "CylinderParameterView" ? CylinderTab : AxisTab;
    }

    private void RoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: SimulationRole role })
        {
            _machine.SelectRole(role.Role);
            if (FlySettings is not null) FlySettings.DataContext = new SettingsDisplay(_historyLocation, _machine.Actor);
        }
    }

    private void ApplyFeedbackClicked(object sender, RoutedEventArgs e)
    {
        if (SimulationMode.SelectedItem is SimulationMode mode && SimulationStatus.SelectedItem is SimulationMode status)
            _machine.SetFeedback(mode.Code, status.Code);
    }

    private void LoseFeedbackClicked(object sender, RoutedEventArgs e) => _machine.LoseFeedback();
    private void CompleteInitializationClicked(object sender, RoutedEventArgs e) => _machine.CompleteInitialization();

    private void DisconnectClicked(object sender, RoutedEventArgs e)
    {
        if (_monitor.SelectedDevice is { } device) _monitor.SetConnection(device, connected: false);
    }

    private void ReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (_monitor.SelectedDevice is { } device) _monitor.SetConnection(device, connected: true);
    }

    private sealed record SettingsDisplay(string? HistoryLocation, string Actor);
}

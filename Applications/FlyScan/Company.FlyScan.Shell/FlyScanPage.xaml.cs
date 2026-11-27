using Company.FlyScan.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Company.FlyScan.Shell;

public partial class FlyScanPage : UserControl, IDisposable
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(50);
    private const string RecipeFilter = "飞拍配方 (*.json)|*.json";
    private static string ExamplePath => Path.Combine(AppContext.BaseDirectory, "Config", "FlyScan.DemoRecipe.json");
    private readonly DispatcherTimer _timer = new() { Interval = Frame };
    private readonly SimulatedScanPeers _peers = new();
    private FlyScanInputs _inputs = new();
    private long _inputConnectionEpoch;
    private SimulatedMachine? _machine;
    private SimulatedIoMonitor? _monitor;
    private FlyScanWorkflow? _flow;
    private FlyScanHistory? _history;
    private TeachGroup[] _draft = EmptyGroups();
    private int _editingGroup;
    private long _lastMachineContext;
    private ScanFlightEvent? _lastDisplayedEvent;
    private string? _journalError;
    private string? _recordError;

    public event EventHandler? AxisPageRequested;

    public FlyScanPage() => InitializeComponent();

    internal void Initialize(SimulatedMachine machine, string journal, FlyScanHistory history, SimulatedIoMonitor monitor)
    {
        _machine = machine;
        _monitor = monitor;
        _lastMachineContext = machine.ContextVersion;
        _history = history;
        try { _flow = new FlyScanWorkflow(machine, journal, history: history); }
        catch (Exception ex) when (IsOperationError(ex))
        {
            _journalError = $"仿真检查点无法打开。{journal}。{ex.Message}";
        }
        ConnectedSignal.IsChecked = machine.CardConnected;
        if (_flow is not null) ReadInputs();
        TryAction(() => ShowRecipe(_flow?.RecipeAwaitingRecovery ?? FlyScanRecipe.Load(ExamplePath)));
        _machine.PropertyChanged += MachineChanged;
        _timer.Tick += (_, _) =>
        {
            ObserveSignals();
            if (AutoStep.IsChecked == true && _flow?.IsRunning == true) Advance(Frame);
            else if (_flow?.RetryPendingCompletion() == true) Refresh();
        };
        ObserveSignals();
        _timer.Start();
        Refresh();
    }

    private void MachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_machine is null) return;
        ConnectedSignal.IsChecked = _machine.CardConnected;
        if (_lastMachineContext != _machine.ContextVersion)
        {
            _lastMachineContext = _machine.ContextVersion;
            if (_flow is { } flow)
            {
                var inputs = ReadInputs();
                if (flow.IsRunning) flow.Tick(TimeSpan.Zero, inputs);
            }
            ObserveSignals();
        }
        Refresh();
    }

    private void NewClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        RequireTeaching();
        RecipeName.Text = "新配方";
        _draft = EmptyGroups();
        ShowGroup();
        PageMessage.Text = "已建立两组示教草稿。";
    });

    private void OpenClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        var dialog = new OpenFileDialog { Filter = RecipeFilter, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ShowRecipe(FlyScanRecipe.Load(dialog.FileName));
    });

    private void ExampleClicked(object sender, RoutedEventArgs e) => TryAction(() => ShowRecipe(FlyScanRecipe.Load(ExamplePath)));

    private void SaveClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        RequireTeaching();
        var recipe = GetRecipe().Validate();
        var dialog = new SaveFileDialog { Filter = RecipeFilter, DefaultExt = ".json", FileName = "FlyScan.Recipe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        recipe.Save(dialog.FileName);
        PageMessage.Text = $"配方已保存至 {dialog.FileName}";
        LogTeaching("保存配方", recipe.Name);
    });

    private void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_machine is null || GroupSelector.SelectedIndex is not (0 or 1) || RobotPose is null ||
            GroupSelector.SelectedIndex == _editingGroup) return;
        StoreHeader();
        _editingGroup = GroupSelector.SelectedIndex;
        ShowGroup();
    }

    private void AxisClicked(object sender, RoutedEventArgs e) => AxisPageRequested?.Invoke(this, EventArgs.Empty);
    private void WaypointClicked(object sender, RoutedEventArgs e) => RecordPoint(trigger: false);
    private void TriggerClicked(object sender, RoutedEventArgs e) => RecordPoint(trigger: true);
    private void RemoveClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        RequireTeaching();
        var group = _draft[_editingGroup];
        if (group.Waypoints.Count == 0) return;
        group.Waypoints.RemoveAt(group.Waypoints.Count - 1);
        group.Triggers.RemoveAll(point => point.Segment >= group.Waypoints.Count);
        ShowPoints();
        PageMessage.Text = "已删除当前组末点及该段触发记录。";
    });

    private void RecordPoint(bool trigger) => TryAction(() =>
    {
        RequireTeaching();
        var group = _draft[_editingGroup];
        int segment = group.Waypoints.Count;
        if (trigger && segment == 0) throw new InvalidOperationException("请先记录本组扫描起点。");
        var point = _machine!.CaptureTeachingPoint();
        if (segment > 0 && group.Waypoints[^1] == point)
            throw new InvalidOperationException("该点与上一点重合，请先移动平台。");
        group.Waypoints.Add(point);
        if (trigger)
        {
            string name = $"T{segment:000}";
            while (group.Triggers.Any(item => item.Name == name)) name += "A";
            group.Triggers.Add(new ScanTrigger(name, segment, point));
        }
        ShowPoints();
        PageMessage.Text = $"已记录 {group.Name} 的{(trigger ? "触发点" : "轨迹点")}。";
        LogTeaching(trigger ? "示教触发点" : "示教轨迹点", $"{group.Name} · X {point.X:G} / Y {point.Y:G} / R1 {point.R1:G}");
    });

    private void RequireTeaching()
    {
        if (_machine?.CanAdjust != true) throw new InvalidOperationException("手动调试及对应角色才能编辑示教配方。");
    }

    private void StoreHeader() => _draft[_editingGroup].Pose = RobotPose.Text.Trim();

    private FlyScanRecipe GetRecipe()
    {
        StoreHeader();
        return new FlyScanRecipe(RecipeName.Text, _draft.Select(group =>
            new ScanGroup(group.Name, group.Pose, group.Waypoints, group.Triggers)).ToArray());
    }

    private void ShowRecipe(FlyScanRecipe recipe)
    {
        RecipeName.Text = recipe.Name;
        _draft = recipe.Groups.Select(group => new TeachGroup(group.Name, group.RobotPose,
            group.Waypoints.ToList(), group.Triggers.ToList())).ToArray();
        if (GroupSelector.SelectedIndex < 0) GroupSelector.SelectedIndex = 0;
        ShowGroup();
        PageMessage.Text = $"已载入「{recipe.Name}」。";
    }

    private void ShowGroup()
    {
        RobotPose.Text = _draft[_editingGroup].Pose;
        ShowPoints();
    }

    private void ShowPoints()
    {
        var group = _draft[_editingGroup];
        Waypoints.ItemsSource = group.Waypoints.Select((point, i) => new PointRow(i == 0 ? "起点" : $"点 {i + 1}", point)).ToArray();
        TriggerTable.ItemsSource = group.Triggers.ToArray();
    }

    private void StartClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        _flow!.Start(GetRecipe(), ReadInputs());
        PageMessage.Text = "双组流程已启动。";
    });

    private void PauseClicked(object sender, RoutedEventArgs e) => Interrupt(ScanInterruption.Paused);
    private void StopClicked(object sender, RoutedEventArgs e) => Interrupt(ScanInterruption.Stopped);
    private void CancelClicked(object sender, RoutedEventArgs e) => Interrupt(ScanInterruption.Cancelled);
    private void RecoverClicked(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        _flow!.Recover(ReadInputs());
        PageMessage.Text = "已确认恢复，未完成组从组起点重做。";
    });

    private void StepClicked(object sender, RoutedEventArgs e) => Advance(Frame);

    private void SignalChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ConnectedSignal))
        {
            _machine?.SetCardConnected(ConnectedSignal.IsChecked == true);
            return;
        }
        ObserveSignals();
        if (_flow?.IsRunning == true) Advance(TimeSpan.Zero);
        else
        {
            if (_flow is not null) ReadInputs();
            Refresh();
        }
    }

    private void ObserveSignals()
    {
        if (_flow is null || _history is null || _machine is null) return;
        try
        {
            _history.Observe(_machine.CardConnected, AlarmSignal.IsChecked == true, EmergencySignal.IsChecked == true);
            _recordError = null;
        }
        catch (SqliteException ex) { _recordError = ex.Message; }
    }

    private void LogTeaching(string action, string detail)
    {
        if (_history is null || _flow is null || _machine is null) return;
        try { _history.Record(new FlyOperation(_history.Now, _machine.Actor, "飞拍配方", action, detail, "已确认")); }
        catch (SqliteException ex) { _recordError = ex.Message; }
    }

    private FlyScanInputs ReadInputs()
    {
        _inputConnectionEpoch = _monitor!.ScanConnectionEpoch;
        return _inputs = _peers.Read(_flow!.Outputs,
            LoadedSignal.IsChecked == true, UnloadedSignal.IsChecked == true, RobotSignal.IsChecked == true,
            ReadySignal.IsChecked == true, _machine!.CardConnected, AlarmSignal.IsChecked == true, EmergencySignal.IsChecked == true);
    }

    private void Interrupt(ScanInterruption reason) => TryAction(() =>
    {
        _flow!.Interrupt(reason, ReadInputs());
        PageMessage.Text = _flow.Message;
    });

    private void Advance(TimeSpan elapsed) => TryAction(() => _flow!.Tick(elapsed, ReadInputs()));

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (IsOperationError(ex)) { PageMessage.Text = ex.Message; }
        Refresh();
    }

    private static bool IsOperationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;

    private void Refresh()
    {
        if (_machine is null) return;
        bool canTeach = _machine.CanAdjust;
        NewRecipe.IsEnabled = SaveRecipe.IsEnabled = WaypointButton.IsEnabled = TriggerButton.IsEnabled = RemovePoint.IsEnabled = canTeach;
        RecipeName.IsReadOnly = RobotPose.IsReadOnly = !canTeach;
        if (_flow is not { } flow)
        {
            StartButton.IsEnabled = StepButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled =
                CancelButton.IsEnabled = RecoverButton.IsEnabled = false;
            RunStatus.Text = _journalError;
            return;
        }
        StartButton.IsEnabled = flow.CanStart;
        PauseButton.IsEnabled = StopButton.IsEnabled = CancelButton.IsEnabled = StepButton.IsEnabled = flow.IsRunning;
        RecoverButton.IsEnabled = flow.Phase == ScanPhase.Interrupted;
        string status = flow.CleanupPending ? $"{flow.Message} 等待确认轨迹清理。" : flow.Message;
        RunStatus.Text = (flow.HistoryError ?? _recordError) is { } recordError ? $"{status} 历史写入异常。{recordError}" : status;
        RunCounts.Text = $"本件确认组 {flow.ConfirmedGroups}/2　尝试 {flow.Attempt}　组内确认触发 {flow.LastConfirmedTrigger}" +
            $"　累计工件 {flow.Counts.Pieces}　累计组 {flow.Counts.Groups}　累计脉冲 {flow.Counts.Triggers}";
        var outputs = flow.Outputs;
        _monitor?.PublishScanIo(_inputConnectionEpoch, _inputs, outputs);
        string ready = _inputs.CardConnected ? OnOff(_inputs.VisionReady) : "未知";
        Handshake.Text = $"上料请求 {OnOff(outputs.RequestLoad.HasValue)}　机器人位号 {outputs.RequestRobot?.Pose ?? "—"}" +
            $"　保持请求 {OnOff(outputs.HoldRobot)}　视觉 Ready {ready}" +
            $"　触发使能 {OnOff(flow.TriggerArmed)}　触发 {OnOff(outputs.VisionTrigger)}　下料请求 {OnOff(outputs.RequestUnload.HasValue)}";
        MotionFeedback.Text = flow.Feedback is { } feedback
            ? $"X {feedback.Position.X:F4} mm    Y {feedback.Position.Y:F4} mm    R1 {feedback.Position.R1:F4}°    R2 {feedback.R2:F4}°" +
              $"    R1 / R2 速度 {feedback.R1Speed:F4} / {feedback.R2Speed:F4} °/s" +
              $"    {(feedback.Busy ? "运动中" : "已停稳")}"
            : "等待插补反馈";
        var records = flow.Events;
        var newest = records.LastOrDefault();
        if (!ReferenceEquals(_lastDisplayedEvent, newest))
        {
            _lastDisplayedEvent = newest;
            FlightEvents.ItemsSource = records.Reverse().Take(40).ToArray();
        }
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    public void Dispose()
    {
        _timer.Stop();
        if (_machine is not null) _machine.PropertyChanged -= MachineChanged;
        try
        {
            if (_flow?.IsRunning == true) _flow.Interrupt(ScanInterruption.Stopped, _inputs);
        }
        finally { _flow?.Dispose(); }
    }

    private static TeachGroup[] EmptyGroups() => [new("第一组", "", [], []), new("第二组", "", [], [])];
    private sealed record PointRow(string Number, ScanPoint Point);
    private sealed class TeachGroup(string name, string pose, List<ScanPoint> waypoints, List<ScanTrigger> triggers)
    {
        internal string Name { get; } = name;
        internal string Pose { get; set; } = pose;
        internal List<ScanPoint> Waypoints { get; } = waypoints;
        internal List<ScanTrigger> Triggers { get; } = triggers;
    }
}

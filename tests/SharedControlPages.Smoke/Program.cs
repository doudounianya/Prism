using Company.FlyScan.Runtime;
using Company.Hardware.ControlCard;
using Company.Hmi.Presentation;
using Company.Platform.Shared.Login;
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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var journal = Path.Combine(Path.GetTempPath(), $"flyscan-wpf-{Guid.NewGuid():N}", "run.json");
        var app = new Company.FlyScan.Shell.App { SimulationJournalPath = journal };
        app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Verify(app.MainWindow!, args.FirstOrDefault());
                app.Shutdown();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        }, DispatcherPriority.ApplicationIdle);
        return app.Run();
    }

    private static async Task Verify(Window window, string? evidence)
    {
        T Named<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        Check(typeof(Company.FlyScan.Shell.App).Assembly.EntryPoint?.DeclaringType?.FullName == "Company.Shell.Program",
            "FlyScan executable uses the common shell entry point");
        var pages = Named<TabControl>("Pages");
        var parameterPages = Named<TabControl>("ParameterPages");
        var diagnostics = Named<Expander>("SimulationDiagnostics");
        TextBlock StatusText(string name) => (TextBlock)pages.Template.FindName(name, pages);
        Check(!diagnostics.IsExpanded && pages.Items.Count == 10 && !Named<TabItem>("MesTab").IsEnabled,
            "HMI navigation and collapsed simulation diagnostics are available at startup");
        Check(StatusText("CardConnection").Text == "运动卡通讯　正常", "status bar shows the initial scan connection");
        var width = window.Width;
        var windowState = window.WindowState;
        window.WindowState = WindowState.Normal;
        window.Width = window.MinWidth;
        await Idle();
        var lastTab = Named<TabItem>("SettingsTab");
        var tabBounds = lastTab.TransformToAncestor(window).TransformBounds(new Rect(lastTab.RenderSize));
        Check(lastTab.IsVisible && tabBounds.Right <= window.ActualWidth,
            "all navigation items fit at minimum window width");
        window.Width = width;
        window.WindowState = windowState;
        var modeTab = Named<TabItem>("ModeTab");
        var modePeer = new TabItemAutomationPeer(modeTab, new TabControlAutomationPeer(pages));
        ((ISelectionItemProvider)modePeer.GetPattern(PatternInterface.SelectionItem)!).Select();
        await Idle();
        Check(ReferenceEquals(pages.SelectedItem, modeTab) &&
            ((Border)modeTab.Template.FindName("ActiveLine", modeTab)).IsVisible,
            "selecting a navigation tab shows its page and the HMI active underline");
        async Task Open(string tab)
        {
            if (tab is "AxisTab" or "CylinderTab")
            {
                pages.SelectedItem = Named<TabItem>("ParametersTab");
                parameterPages.SelectedItem = Named<TabItem>(tab);
            }
            else pages.SelectedItem = Named<TabItem>(tab);
            await Idle();
        }
        var roles = Named<ComboBox>("SimulationRole");
        var displayMode = Named<ComboBox>("SimulationMode");
        var displayStatus = Named<ComboBox>("SimulationStatus");
        async Task Feedback(int mode, int status)
        {
            displayMode.SelectedItem = SimulatedMachine.Modes.Single(item => item.Code == mode);
            displayStatus.SelectedItem = SimulatedMachine.States.Single(item => item.Code == status);
            Click(window, "应用反馈");
            await Idle();
        }
        async Task Role(UserRole? role)
        {
            roles.SelectedItem = SimulatedMachine.Roles.Single(item => item.Role == role);
            await Idle();
        }

        await Open("ModeTab");
        var mode = Walk<ModeControlView>(window).Single();
        var modeModel = (SimulatedModePage)mode.DataContext;
        RadioButton Diagram(string name) => (RadioButton)mode.FindName(name + "Tab");
        foreach (var item in new[] { (0, 0, "Manual"), (0, 10, "Manual"), (0, 20, "Manual"),
                     (0, 30, "Manual"), (0, 40, "Manual"), (20, 50, "Automatic"),
                     (10, 60, "Maintenance"), (0, 70, "Emergency") })
        {
            await Feedback(item.Item1, item.Item2);
            Check(Diagram(item.Item3).IsChecked == true && modeModel.ConfirmedState == (item.Item1, item.Item2),
                $"confirmed feedback {item.Item1}/{item.Item2} selects {item.Item3}");
        }
        await Feedback(0, 0);
        Diagram("Automatic").IsChecked = true;
        await Feedback(0, 0);
        Check(Diagram("Automatic").IsChecked == true && modeModel.Display.Mode == 0,
            "stable confirmed feedback preserves manual browse selection");
        await Feedback(0, 30);
        Check(Diagram("Manual").IsChecked == true, "same-mode state change follows actual diagram");
        Check(StatusText("EquipmentMode").Text == "本机模式　手动模式" &&
            StatusText("EquipmentState").Text == "本机状态　调试" &&
            StatusText("ParameterAccess").Text == "参数只读", "status bar shows confirmed state and visitor access");
        Click(window, "反馈未知");
        await Idle();
        Check(StatusText("EquipmentState").Text == "本机状态　未知", "status bar drops stale feedback");
        Check(modeModel.ConfirmedState is null && Diagram("Manual").IsChecked == true, "unknown feedback does not navigate");
        await Role(UserRole.EquipmentEngineer);
        await Feedback(0, 0);
        await modeModel.PressInitializationAsync();
        Check(modeModel.Display.Status == 40 && modeModel.Display.Action(7) == true, "initialization press in valid context");
        await modeModel.ReleaseInitializationAsync();
        Check(modeModel.Display.Action(7) == false, "initialization release clears simulated request");
        Click(window, "初始化完成");
        await Idle();
        Check(modeModel.Display.Status == 0 && modeModel.Display.Signal(0) == true, "completion uses explicit simulated feedback");
        await Feedback(0, 30);
        Check(StatusText("ParameterAccess").Text == "手动调试 · 参数可操作", "status bar reflects authorized debugging");
        await Feedback(0, 0);
        Check(StatusText("ParameterAccess").Text == "参数只读", "status bar returns to read-only after debugging");
        diagnostics.IsExpanded = false;
        await Idle();
        Capture(window,evidence,"fly-mode");

        await Open("AxisTab");
        var axisView = Walk<AxisParameterView>(window).Single();
        var axis = (SimulatedAxisPage)axisView.DataContext;
        Check(axis.Components.Count == 13 && axis.Components.Select(option => option.Axis.HardwareAxis).SequenceEqual(Enumerable.Range(0, 13)),
            "configuration loads thirteen mapped axes");
        Check(axis.Components.Take(4).All(option => option.Axis.Bus == MotionBusKind.Localbus)
            && axis.Components.Skip(4).All(option => option.Axis.Bus == MotionBusKind.EtherCAT), "Localbus and EtherCAT axis split");
        var axisSelector = Walk<ComboBox>(axisView).Single(c => ReferenceEquals(c.ItemsSource, axis.Components));
        Check(axisSelector.Items.Count == 13, "shared axis selector renders thirteen axes");
        await Feedback(0, 30);
        Check(ReferenceEquals(pages.SelectedItem, Named<TabItem>("ParametersTab")) &&
            ReferenceEquals(parameterPages.SelectedItem, Named<TabItem>("AxisTab")), "feedback cannot switch outer page");
        foreach (var role in SimulatedMachine.Roles)
        {
            await Role(role.Role);
            bool granted = role.Role is UserRole.Administrator or UserRole.EquipmentEngineer or UserRole.ProcessEngineer;
            Check(axis.CanUseControls == granted && (axis.BeginEntry(axis.AbsolutePositions[0].Position) is not null) == granted,
                "manual-debug role " + role.Name);
        }
        await Feedback(20, 50);
        Check(!axis.CanOperate, "automatic running stays read-only");
        await Feedback(10, 60);
        Check(!axis.CanOperate, "maintenance stays read-only");
        await Feedback(0, 70);
        Check(!axis.CanOperate, "emergency stays read-only");
        await Feedback(0, 30);
        var target = axis.AbsolutePositions[0];
        var revoked = axis.BeginEntry(target.Position)!;
        await Role(UserRole.Operator);
        await revoked.CommitAsync("14");
        Check(target.Target == 0 && !axis.CanOperate, "permission changes invalidate an already-open entry");
        await Role(UserRole.Administrator);
        var stale = axis.BeginEntry(target.Position)!;
        Click(window, "反馈未知");
        await Idle();
        await stale.CommitAsync("15");
        Check(!axis.CanOperate && target.Target == 0, "unknown feedback invalidates an already-open entry");
        await Feedback(0, 30);
        var oldAxis = axis.BeginEntry(target.Position)!;
        axisSelector.SelectedIndex = 2;
        await Idle();
        await oldAxis.CommitAsync("16");
        Check(axis.PositionUnit == "°" && target.Target == 0, "axis change invalidates entry and updates angular unit");
        axisSelector.SelectedIndex = 3;
        await Idle();
        Check(!axis.CanOperate && axis.WriteModeText.Contains("从轴") && axis.BeginEntry(axis.AbsolutePositions[0].Position) is null,
            "rotation follower offers feedback only");
        axisSelector.SelectedIndex = 0;
        await Idle();
        var edit = axis.BeginEntry(target.Position)!;
        Check(edit.Validate("NaN") is not null && edit.Validate("Infinity") is not null, "entry rejects non-finite numbers");
        await edit.CommitAsync("3.5");
        Check(target.Target == 3.5 && axis.PositionUnit == "mm", "valid local positioning target");
        Click(axisView, "使能");
        await Idle();
        Check(axis.MotorState.BooleanValue == true, "shared enable command reads simulator feedback");
        await axis.MoveAsync(target);
        await Wait(() => axis.ActualPosition.Value == "3.5", "positioning advances to the configured target");
        await axis.PressMomentaryAsync("Positive");
        await Wait(() => axis.ActualVelocity.Value != "0", "jog starts under manual debug");
        await Role(UserRole.Operator);
        await Wait(() => axis.ActualVelocity.Value == "0", "changing role stops the original jog");
        await Role(UserRole.Administrator);
        Capture(window,evidence,"fly-axis");
        Click(axisView, "气缸参数");
        await Idle();
        var cylinder = Walk<CylinderParameterView>(window).Single();
        var cylinderModel = (SimulatedCylinderPage)cylinder.DataContext;
        Check(cylinderModel.CurrentState == "待配置" && !cylinderModel.CanUseControls
            && Walk<Button>(cylinder).Where(b => Equals(b.Content,"缩回") || Equals(b.Content,"伸出")).All(b => !b.IsEnabled),
            "unmapped cylinder has no enabled output actions");
        Capture(window,evidence,"fly-cylinder");
        Click(cylinder, "轴参数");
        await Idle();
        Check(ReferenceEquals(parameterPages.SelectedItem, Named<TabItem>("AxisTab")), "shared subnavigation returns to axis page");
        await Feedback(20, 50);
        await Open("ModeTab");
        Check(Diagram("Automatic").IsChecked == true && modeModel.Display.Status == 50,
            "re-entered mode page follows confirmed automatic feedback");

        await Open("FlyScanTab");
        var scan = Named<Company.FlyScan.Shell.FlyScanPage>("ScanPage");
        T Scan<T>(string name) where T : FrameworkElement => (T)scan.FindName(name);
        await Feedback(0, 30);
        Click(scan, "新建配方");
        await Wait(() => Scan<TextBlock>("PageMessage").Text == "已建立两组示教草稿。", "WPF creates a fresh teaching recipe");
        Click(scan, "记录轨迹点");
        await Wait(() => Scan<DataGrid>("Waypoints").Items.Count == 1, "WPF teaching button records settled XYR1 feedback");
        Click(scan, "载入示例");
        await Wait(() => Scan<TextBox>("RobotPose").Text == "P01" && Scan<DataGrid>("TriggerTable").Items.Count == 3,
            "WPF sample recipe retains the first robot pose and three trigger points");
        Capture(window, evidence, "fly-teach");
        await Feedback(20, 0);
        Scan<TabControl>("WorkTabs").SelectedIndex = 1;
        Scan<CheckBox>("AutoStep").IsChecked = false;
        await Idle();
        Click(scan, "开始双组扫描");
        await Idle();
        Check(modeModel.Display.Status == 50, "WPF workflow publishes running feedback to the shared mode page");
        for (int i = 0; i < 30 && !Scan<TextBlock>("Handshake").Text.Contains("触发使能 ON", StringComparison.Ordinal); i++)
        {
            Click(scan, "单步");
            await Idle();
        }
        Check(Scan<TextBlock>("Handshake").Text.Contains("保持请求 ON", StringComparison.Ordinal),
            "WPF sim shows robot hold while acquiring the first group");

        var ready = Scan<CheckBox>("ReadySignal");
        ready.IsChecked = false;
        ready.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ready));
        await Idle();
        Check(Scan<Button>("RecoverButton").IsEnabled && !Scan<Button>("StepButton").IsEnabled &&
            Scan<TextBlock>("Handshake").Text.Contains("触发使能 OFF", StringComparison.Ordinal),
            "WPF Ready loss interrupts acquisition and withdraws trigger");
        Capture(window, evidence, "fly-interrupted");
        ready.IsChecked = true;
        ready.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ready));
        Click(scan, "确认恢复");
        await Idle();
        for (int i = 0; i < 80 && Scan<Button>("StepButton").IsEnabled; i++)
        {
            Click(scan, "单步");
            await Idle();
        }
        Check(Scan<TextBlock>("RunCounts").Text.Contains("累计工件 1", StringComparison.Ordinal) &&
            Scan<TextBlock>("RunCounts").Text.Contains("累计组 2", StringComparison.Ordinal) && modeModel.Display.Status == 0,
            "WPF explicit recovery completes two groups and returns shared mode to stopped");
        Capture(window, evidence, "fly-completed");

        Check(pages.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(new[]
        {
            "设备监控", "模式控制", "IO监控", "参数设置", "报警监控", "生产数据", "工艺参数", "操作日志", "MES接口", "系统设置"
        }) && !Named<TabItem>("MesTab").IsEnabled, "G4 retains the ten established navigation entries and disabled MES");

        void Signal(string name, bool enabled)
        {
            var toggle = Scan<CheckBox>(name);
            toggle.IsChecked = enabled;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
        }
        static string? Field(object row, string property) => row.GetType().GetProperty(property)?.GetValue(row)?.ToString();
        static DataGrid Table(DependencyObject page) => Walk<DataGrid>(page).First();
        Signal("AlarmSignal", true);
        await Open("AlarmsTab");
        var realtime = Named<RealtimeAlarmView>("RealtimePage");
        await Wait(() => Table(realtime).Items.Cast<object>().Any(row => Field(row, "StateText") == "活动"),
            "G4 shared real-time alarm reads the active motion-card signal");
        Signal("ConnectedSignal", false);
        await Wait(() => Table(realtime).Items.Cast<object>().Count(row => Field(row, "StateText") == "未知") == 2,
            "G4 shared real-time alarm distinguishes both unknown signals after disconnect");
        Check(StatusText("CardConnection").Text == "运动卡通讯　断开", "status bar shows the scan disconnect");
        Signal("AlarmSignal", false);
        Signal("ConnectedSignal", true);
        Click(realtime, "历史报警");
        await Idle();
        Check(StatusText("CardConnection").Text == "运动卡通讯　正常", "status bar shows the scan reconnect");
        var alarmHistory = Named<AlarmHistoryView>("AlarmHistoryPage");
        Check(alarmHistory.IsVisible && Table(alarmHistory).Items.Cast<object>().Any(row =>
            Field(row, "ObservedDuration")?.Contains("缺口未知", StringComparison.Ordinal) == true),
            "G4 shared alarm history shows observed duration and uncertain gap");
        Capture(window, evidence, "fly-alarm-history");
        Click(alarmHistory, "报警分析");
        await Idle();
        var ranking = Named<AlarmAnalysisView>("AlarmAnalysisPage");
        Check(ranking.IsVisible && Table(ranking).Items.Cast<object>().Any(row => Field(row, "Code") == "CARD"),
            "G4 alarm analysis renders the same persisted alarm code");
        Capture(window, evidence, "fly-alarm-analysis");
        await Open("ProductionTab");
        var today = Named<ProductionTodayView>("TodayPage");
        Check(Walk<DataGrid>(today).SelectMany(table => table.Items.Cast<object>()).Count(row => Field(row, "Quantity") == "1") == 1,
            "G4 today page shows one completed simulated workpiece in its hour");
        Click(today, "生产日报");
        await Idle();
        var daily = Named<ProductionDailyView>("DailyPage");
        Check(daily.IsVisible && Walk<DataGrid>(daily).Count() == 2, "G4 daily page preserves the two shift tables");
        Click(daily, "产量趋势");
        await Idle();
        var trend = Named<ProductionTrendView>("TrendPage");
        Check(trend.IsVisible && Table(trend).Items.Cast<object>().Any(row => Field(row, "Total") == "1"),
            "G4 trend chart and table use the persisted daily totals");
        Capture(window, evidence, "fly-production-trend");
        var selectedTrend = Table(trend).Items.Cast<object>().Single(row => Field(row, "Total") == "1");
        Table(trend).SelectedItem = selectedTrend;
        Click(trend, "查看选中日期日报");
        await Idle();
        Check(daily.IsVisible, "G4 production trend drills down to the selected daily report");
        await Open("LogTab");
        var operation = Named<OperationLogView>("LogPage");
        Check(Table(operation).Items.Cast<object>().Any(row => Field(row, "Operation") == "示教轨迹点") &&
            Table(operation).Items.Cast<object>().Count(row => Field(row, "Operation") == "工件完成") == 1,
            "G4 shared log shows the teaching action and a single completion");
        Capture(window, evidence, "fly-operation-log");
        await Open("DeviceTab");
        Check(Table(Named<DeviceMonitorView>("DevicePage")).Items.Cast<object>().Any(row => Field(row, "State") == "通讯正常"),
            "G4 device monitoring shows the live simulator channel");
        await Open("SettingsTab");
        var settings = Named<SettingsPage>("FlySettings");
        Check(Walk<TextBlock>(settings).Any(item => item.Text == "历史数据目录"), "G4 settings shows the isolated history directory");
        var settingTabs = Walk<TabControl>(settings).Single();
        settingTabs.SelectedIndex = 1;
        await Idle();
        Check(Walk<TextBlock>(settings).Any(item => item.Text == "管理员"), "G4 shared account settings shows the current simulation role");
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> condition, string check)
    {
        for (int n = 0; n < 100 && !condition(); n++) await Task.Delay(25);
        Check(condition(), check);
    }

    private static void Check(bool value, string check)
    {
        if (!value) throw new InvalidOperationException(check);
        Console.WriteLine("PASS " + check);
    }

    private static void Click(DependencyObject view, string caption)
    {
        if (view is Window window && window.FindName("SimulationDiagnostics") is Expander { IsExpanded: false } diagnostics)
        {
            diagnostics.IsExpanded = true;
            window.UpdateLayout();
        }
        var button = Walk<Button>(view).Single(b => b.IsVisible && Equals(b.Content, caption));
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Capture(Window window, string? path, string name)
    {
        if (path is null) return;
        Directory.CreateDirectory(path);
        if (window.FindName("SimulationDiagnostics") is Expander diagnostics)
        {
            diagnostics.IsExpanded = false;
            window.UpdateLayout();
        }
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(path, name + ".png"));
        png.Save(file);
    }
}

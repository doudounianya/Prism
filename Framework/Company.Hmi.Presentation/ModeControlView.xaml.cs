using Company.Hmi.Contracts;
/*
 * 文件职责与维护说明：
 * 模式控制页面展示用户确认的三张状态切换图，复用现有受登录权限控制的导航入口。
 * KeepAlive 保留页面及四个图形实例；页签仅切换可见性，不重新导航、不重建图形。
 * 页面可见性通知视图模型管理原有按页采集；视图事件本身不读写硬件，也不模拟状态转移。
 */
using System.Windows;
using System.Windows.Controls;
using Company.Logger;
using System.Windows.Media;
using System.ComponentModel;
namespace Company.Hmi.Presentation;
public partial class ModeControlView : UserControl
{
    private Window? _window;
    private INotifyPropertyChanged? _modeSource;
    private (int Mode, int Status)? _lastConfirmedState;
    public ModeControlView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => ObserveModeSource(e.NewValue);
        ObserveModeSource(DataContext);
        Loaded += (_, _) => { _window = Window.GetWindow(this); if (_window is not null) { _window.StateChanged += WindowStateChanged; _window.Deactivated += WindowDeactivated; } UpdateVisibility(); };
        Unloaded += (_, _) => { ModePressButton.CancelAll(this); if (_window is not null) { _window.StateChanged -= WindowStateChanged; _window.Deactivated -= WindowDeactivated; } _window = null; UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    /// <summary>订阅当前视图模型的有效模式目标，不订阅 PLC、不建立新的采集或计时器。</summary>
    private void ObserveModeSource(object? source)
    {
        if (_modeSource is not null) _modeSource.PropertyChanged -= ModeSourceChanged;
        _modeSource = source as INotifyPropertyChanged; _lastConfirmedState = null;
        if (_modeSource is not null) _modeSource.PropertyChanged += ModeSourceChanged;
        FollowConfirmedDiagram();
    }
    private void ModeSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IModeControlPage.ConfirmedState)) FollowConfirmedDiagram();
    }
    /// <summary>首次有效读取或实际模式/状态变化时跟随；稳定刷新不抢占人工查看的页签。
    /// 空目标表示交接/未知/断线，保留页面与上次已确认目标，不能借缓存触发切换。</summary>
    private void FollowConfirmedDiagram()
    {
        if (!IsLoaded || !IsVisible || _window?.WindowState == WindowState.Minimized ||
            DataContext is not IModeControlPage { ConfirmedDiagram: { } target, ConfirmedState: { } state } ||
            state == _lastConfirmedState) return;
        var tab = target switch { "Manual" => ManualTab, "Automatic" => AutomaticTab, "Maintenance" => MaintenanceTab, "Emergency" => EmergencyTab, _ => null };
        if (tab is null) return;
        _lastConfirmedState = state;
        // 维护页内状态发生变化时也回到说明图；状态稳定时不会打断人员锁管理。
        if (target == "Maintenance") MaintenanceIntroTab.IsChecked = true;
        tab.IsChecked = true;
        DiagramScroll?.ScrollToTop();
    }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    private async void WindowDeactivated(object? sender, EventArgs e)
    { ModePressButton.CancelAll(this); if (DataContext is IModeControlPage vm) await vm.ReleaseMomentaryRequestsAsync(); }
    private async void InitializationPressed(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not IModeControlPage vm) return;
        if (e.OriginalSource is Button { Tag: "Stop" }) await vm.PressStopAsync();
        else await vm.PressInitializationAsync();
    }
    private async void InitializationReleased(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not IModeControlPage vm) return;
        if (e.OriginalSource is Button { Tag: "Stop" }) await vm.ReleaseStopAsync();
        else await vm.ReleaseInitializationAsync();
    }
    private async void UpdateVisibility()
    {
        try { if (DataContext is IModeControlPage vm) await vm.SetVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); FollowConfirmedDiagram(); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private async void ModeActionRequested(object? sender, ModeActionEventArgs e)
    {
        try
        {
            if (e.Action.StartsWith("Maintenance", StringComparison.Ordinal)) { MaintenanceTab.IsChecked = true; MaintenancePeopleTab.IsChecked = true; return; }
            if (DataContext is IModeControlPage vm) await vm.ExecuteAsync(e.Action);
        }
        catch (Exception ex) { Logs.LogError(ex); }
    }

    /// <summary>下方显式操作按钮只转交现有业务入口，不能直接写物理信号或跳过权限、急停和维护锁检查。</summary>
    private async void InterfaceActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IModeRequestRow item } || DataContext is not IModeControlPage vm) return;
        try
        {
            if (item.Suffix == "Action[8]") { MaintenanceTab.IsChecked = true; MaintenancePeopleTab.IsChecked = true; return; }
            if (item.Suffix == "Action[6]") await vm.ExecuteAsync(vm.Feedback.Action(6) == true ? "DebugExit" : "DebugEnter");
            else if (item.Suffix == "Action[7]") await vm.ExecuteAsync("Initialize");
        }
        catch (Exception ex) { Logs.LogError(ex); }
    }

    /// <summary>
    /// 只选择当前要查看的模式示意图。实际设备模式仍由现场手自动旋钮和 PLC 决定，
    /// 这里不更改登录会话、设备状态、PLC 数据或其他页面的采集范围。
    /// </summary>
    private void ModeTabChecked(object sender, RoutedEventArgs e)
    {
        // XAML 初始化中第一个 RadioButton 的 Checked 早于下方图形创建，默认可见性由 XAML 给定。
        if (ManualDiagram is null || AutomaticDiagram is null || MaintenanceDiagram is null || EmergencyDiagram is null) return;
        var selected = (sender as RadioButton)?.Tag as string;
        // 每次从其他模式返回维护页先显示介绍图；人员表和图都常驻，仅切换可见性。
        if (selected == "Maintenance" && MaintenanceIntroTab is not null) MaintenanceIntroTab.IsChecked = true;
        ShowModePane(selected);
    }

    private void MaintenancePaneChecked(object sender, RoutedEventArgs e)
    {
        if (MaintenanceTab?.IsChecked == true && MaintenanceDiagram is not null) ShowModePane("Maintenance");
    }

    private void ShowModePane(string? selected)
    {
        bool maintenance = selected == "Maintenance";
        ModePressButton.CancelAll(this);
        bool people = maintenance && SupportsPersonnelLocks && MaintenancePeopleTab?.IsChecked == true;
        ManualDiagram.Visibility = selected == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        AutomaticDiagram.Visibility = selected == "Automatic" ? Visibility.Visible : Visibility.Collapsed;
        EmergencyDiagram.Visibility = selected == "Emergency" ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceDiagram.Visibility = maintenance && !people ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceLockPanel is not null) MaintenanceLockPanel.Visibility = people ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceGuideHeader is not null) MaintenanceGuideHeader.Visibility = maintenance && SupportsPersonnelLocks ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceInstructions is not null) MaintenanceInstructions.Visibility = maintenance && !people ? Visibility.Visible : Visibility.Collapsed;
        // 模式图与急停说明共用同一组反馈控件；维护介绍和人员锁管理中也保留，避免失去查看/解锁入口。
        if (ModeSignalPanel is not null) ModeSignalPanel.Visibility = Visibility.Visible;
        if (ModeLegend is not null) ModeLegend.Visibility = people ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool SupportsPersonnelLocks => (DataContext as IModeControlPage)?.Feedback.SupportsPersonnelLocks == true;

    public event Action<object?, bool>? MaintenanceVerificationRequested;
    private void MaintenanceAddClicked(object sender, RoutedEventArgs e) => MaintenanceVerificationRequested?.Invoke(null, false);
    private void MaintenanceUnlockClicked(object sender, RoutedEventArgs e) => MaintenanceVerificationRequested?.Invoke((sender as Button)?.Tag, false);
    private void MaintenanceAdminUnlockClicked(object sender, RoutedEventArgs e) => MaintenanceVerificationRequested?.Invoke(null, true);
}

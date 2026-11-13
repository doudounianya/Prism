/*
 * 文件职责与维护说明：
 * 模式控制页面展示用户确认的三张状态切换图，复用现有受登录权限控制的导航入口。
 * KeepAlive 保留页面及四个图形实例；页签仅切换可见性，不重新导航、不重建图形。
 * 页面可见性通知视图模型管理原有按页采集；视图事件本身不读写硬件，也不模拟状态转移。
 */
using System.Windows;
using System.Windows.Controls;
using Company.Application.Main.Presentation;
using Company.Application.Main.ViewModels;
using Company.Logger;
using Prism.Navigation.Regions;
using Company.Application.Share.Login;
using System.Windows.Media;
using Company.Application.Main.Models;
using System.ComponentModel;
namespace Company.Application.Main.Views;
public partial class ModeControlView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => true;
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
        if (e.PropertyName is null or nameof(ModeControlViewModel.ConfirmedState)) FollowConfirmedDiagram();
    }
    /// <summary>首次有效读取或实际模式/状态变化时跟随；稳定刷新不抢占人工查看的页签。
    /// 空目标表示交接/未知/断线，保留页面与上次已确认目标，不能借缓存触发切换。</summary>
    private void FollowConfirmedDiagram()
    {
        if (DataContext is not ModeControlViewModel { ConfirmedDiagram: { } target, ConfirmedState: { } state } || state == _lastConfirmedState) return;
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
    { ModePressButton.CancelAll(this); if (DataContext is ModeControlViewModel vm) await vm.ReleaseInitializationAsync(); }
    private async void InitializationPressed(object sender, RoutedEventArgs e)
    { e.Handled = true; if (DataContext is ModeControlViewModel vm) await vm.PressInitializationAsync(); }
    private async void InitializationReleased(object sender, RoutedEventArgs e)
    { e.Handled = true; if (DataContext is ModeControlViewModel vm) await vm.ReleaseInitializationAsync(); }
    private async void UpdateVisibility()
    {
        try { if (DataContext is ModeControlViewModel vm) await vm.SetVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private async void ModeActionRequested(object? sender, ModeActionEventArgs e)
    {
        try
        {
            if (e.Action.StartsWith("Maintenance", StringComparison.Ordinal)) { MaintenanceTab.IsChecked = true; MaintenancePeopleTab.IsChecked = true; return; }
            if (DataContext is ModeControlViewModel vm) await vm.ExecuteAsync(e.Action);
        }
        catch (Exception ex) { Logs.LogError(ex); }
    }

    /// <summary>下方显式操作按钮只转交现有业务入口，不能直接写物理信号或跳过权限、急停和维护锁检查。</summary>
    private async void InterfaceActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModeSignalItem item } || DataContext is not ModeControlViewModel vm) return;
        try
        {
            if (item.Suffix == "Action[8]") { MaintenanceTab.IsChecked = true; MaintenancePeopleTab.IsChecked = true; return; }
            if (item.Suffix == "Action[6]") await vm.ExecuteAsync(vm.Display.Action(6) == true ? "DebugExit" : "DebugEnter");
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
        bool people = maintenance && MaintenancePeopleTab?.IsChecked == true;
        ManualDiagram.Visibility = selected == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        AutomaticDiagram.Visibility = selected == "Automatic" ? Visibility.Visible : Visibility.Collapsed;
        EmergencyDiagram.Visibility = selected == "Emergency" ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceDiagram.Visibility = maintenance && !people ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceLockPanel is not null) MaintenanceLockPanel.Visibility = people ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceGuideHeader is not null) MaintenanceGuideHeader.Visibility = maintenance ? Visibility.Visible : Visibility.Collapsed;
        if (MaintenanceInstructions is not null) MaintenanceInstructions.Visibility = maintenance && !people ? Visibility.Visible : Visibility.Collapsed;
        // 模式图与急停说明共用同一组反馈控件；维护介绍和人员锁管理中也保留，避免失去查看/解锁入口。
        if (ModeSignalPanel is not null) ModeSignalPanel.Visibility = Visibility.Visible;
        if (ModeLegend is not null) ModeLegend.Visibility = people ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MaintenanceAddClicked(object sender, RoutedEventArgs e) => ShowMaintenanceVerification(null);
    private void MaintenanceUnlockClicked(object sender, RoutedEventArgs e) => ShowMaintenanceVerification((sender as Button)?.Tag as MaintenanceInterlock.Person);
    private void MaintenanceAdminUnlockClicked(object sender, RoutedEventArgs e) => ShowMaintenanceVerification(null, true);

    /// <summary>维护专用凭据验证窗口。只把密码交给既有账号服务，关闭及提交后立即清空；
    /// 不修改登录会话。最后一人额外提示退出结果需由 PLC 确认，失败保留锁供本人重试。</summary>
    private void ShowMaintenanceVerification(MaintenanceInterlock.Person? person, bool administratorAll = false)
    {
        if (DataContext is not ModeControlViewModel vm) return;
        var owner = Window.GetWindow(this);
        var dialog = new Window { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = administratorAll ? "管理员解除全部维护锁" : person is null ? "人员上锁" : "本人解锁", Width = 500, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontSize = 18 };
        var panel = new StackPanel { Margin = new Thickness(26) };
        void Label(string value) => panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });
        Label(administratorAll ? "此操作将解除所有人员的维护锁，必须重新输入管理员账户和密码。" : person is null ? "输入本人账户，上锁不会切换当前登录用户。" : "只能验证上锁者本人账户，解除本人的人员锁。");
        Label("用户名");
        var name = new TextBox { Text = person?.UserName ?? "", IsReadOnly = person is not null, MinHeight = 44, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(name); Label("密码");
        var password = new PasswordBox { MinHeight = 44, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(password);
        if (administratorAll) Label("确认解除全部锁后将申请退出维护。管理员身份、全部持锁人员及操作结果会写入操作日志；PLC 未确认退出前保持设备操作禁用。");
        if (person is not null && MaintenanceInterlock.People.Length == 1)
            Label("这是最后一把人员锁。验证后申请退出维护，PLC 确认退出前设备操作继续禁用，不会自动运行设备。");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", MinWidth = 96, MinHeight = 44, Margin = new Thickness(0, 0, 12, 0) };
        var accept = new Button { Content = administratorAll ? "管理员确认全部解锁" : person is null ? "验证并上锁" : "验证并解锁", MinWidth = 150, MinHeight = 44, IsDefault = !administratorAll };
        bool submitting = false;
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, args) => { if (submitting) args.Cancel = true; };
        dialog.Closed += (_, _) => password.Clear();
        accept.Click += async (_, _) =>
        {
            if (submitting) return;
            submitting = true; accept.IsEnabled = false; cancel.IsEnabled = false; message.Text = "正在验证身份并核对维护请求…";
            try { await vm.MaintainAsync(name.Text, password.Password, person?.Id, administratorAll); submitting = false; dialog.Close(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { password.Clear(); submitting = false; accept.IsEnabled = true; cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.Loaded += (_, _) => { if (person is null) name.Focus(); else password.Focus(); };
        dialog.ShowDialog();
    }
}

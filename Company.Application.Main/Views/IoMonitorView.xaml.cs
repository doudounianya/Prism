/*
 * 文件职责与维护说明：
 * IO 监控视图在加载、显隐和主窗口最小化变化时通知视图模型，控制当前组的需求范围。
 * 用户刷新交给视图模型执行，不在 WPF 事件中直接访问硬件。
 */
using Company.Application.Main.ViewModels;
using Company.Logger;
using System.Windows;
using System.Windows.Controls;

namespace Company.Application.Main.Views;

public partial class IoMonitorView : UserControl
{
    private Window? _window;
    public IoMonitorView()
    {
        InitializeComponent();
        Loaded += (_, _) => { _window = Window.GetWindow(this); if (_window is not null) _window.StateChanged += WindowStateChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { if (_window is not null) _window.StateChanged -= WindowStateChanged; _window = null; UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    private async void UpdateVisibility()
    {
        try { if (DataContext is IoMonitorViewModel vm) await vm.SetViewVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        try { if (DataContext is IoMonitorViewModel vm) await vm.RefreshAsync(); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
}

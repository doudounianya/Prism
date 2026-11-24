using Company.Hmi.Contracts;
/*
 * 文件职责与维护说明：
 * 轴参数视图将按钮、分页和数字键盘交互转交视图模型。
 * 按住类按钮捕获鼠标，松开、失去捕获或窗口失活时尝试释放原命令，避免界面事件丢失后继续保持请求。
 */
using Company.Logger;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Company.Hmi.Presentation;

public partial class AxisParameterView : UserControl
{
    public event Action<string>? ParameterPageRequested;

    private void ParameterPageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } && page != "current")
            ParameterPageRequested?.Invoke(page);
    }

    private Window? _window;
    private bool _parameterDialogOpen;
    public AxisParameterView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Detach(); _window = Window.GetWindow(this); if (_window is not null) { _window.StateChanged += WindowStateChanged; _window.Deactivated += WindowDeactivated; } UpdateVisibility(); };
        Unloaded += (_, _) => { Detach(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    private void Detach() { if (_window is not null) { _window.StateChanged -= WindowStateChanged; _window.Deactivated -= WindowDeactivated; } _window = null; }
    private async void WindowDeactivated(object? sender, EventArgs e) { if (DataContext is IAxisParameterPage vm) await vm.ReleaseJogAsync(); }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    private async void UpdateVisibility()
    {
        try { if (DataContext is IAxisParameterPage vm) await vm.SetViewVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private async void OperationClicked(object sender, RoutedEventArgs e)
    { if (DataContext is IAxisParameterPage vm && sender is Button { Tag: string action }) await vm.OperateAsync(action); }
    private async void MoveClicked(object sender, RoutedEventArgs e)
    { if (DataContext is IAxisParameterPage vm && sender is Button { DataContext: { } row }) await vm.MoveAsync(row); }
    private async void ParameterPressed(object sender, MouseButtonEventArgs e)
    { e.Handled = true; if (sender is TextBox { DataContext: { } field }) await EditParameterAsync(field); }
    private async void ParameterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        if (!e.IsRepeat && sender is TextBox { DataContext: { } field }) await EditParameterAsync(field);
    }
    // 防止重复打开键盘；先释放按住命令，再获取本次输入的提交上下文。
    // 弹窗只是收集输入，确认后仍由视图模型检查权限、页面代次和连接来源。
    private async Task EditParameterAsync(object field)
    {
        if (_parameterDialogOpen || DataContext is not IAxisParameterPage vm) return;
        _parameterDialogOpen = true;
        try
        {
            await vm.ReleaseJogAsync();
            if (vm.BeginEntry(field) is not { } entry) return;
            var dialog = new ParameterEntryDialog(entry) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) await entry.CommitAsync(dialog.InputValue);
        }
        catch (Exception ex) { Logs.LogError(ex); }
        finally { _parameterDialogOpen = false; }
    }
    // 先取得鼠标捕获才允许按住请求；鼠标离开控件后仍能收到释放相关事件。
    private async void JogPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || DataContext is not IAxisParameterPage vm || !vm.CanOperate) return;
        e.Handled = true;
        if (!button.CaptureMouse()) return;
        await vm.PressMomentaryAsync((string)button.Tag);
    }
    // 释放鼠标捕获并请求业务会话复位，不依赖界面当前选择重新寻找目标。
    private async void JogReleased(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; if (sender is Button button) button.ReleaseMouseCapture();
        if (DataContext is IAxisParameterPage vm) await vm.ReleaseJogAsync();
    }
    private async void JogCaptureLost(object sender, MouseEventArgs e) { if (DataContext is IAxisParameterPage vm) await vm.ReleaseJogAsync(); }
    private async void JogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || e.IsRepeat) return;
        e.Handled = true;
        if (DataContext is IAxisParameterPage vm && sender is Button button) await vm.PressMomentaryAsync((string)button.Tag);
    }
    private async void JogKeyUp(object sender, KeyEventArgs e)
    { if (e.Key is not (Key.Space or Key.Enter)) return; e.Handled = true; if (DataContext is IAxisParameterPage vm) await vm.ReleaseJogAsync(); }
    private async void JogKeyboardLost(object sender, KeyboardFocusChangedEventArgs e) { if (DataContext is IAxisParameterPage vm) await vm.ReleaseJogAsync(); }
}

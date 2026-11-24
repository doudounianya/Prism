using Company.Hmi.Contracts;
/*
 * 文件职责与维护说明：
 * 气缸视图负责可见性通知、伸缩/复位按钮及参数键盘入口。
 * 方向命令的复位顺序、写入权限和通信结果由业务操作会话统一检查，界面不直接拼接 PLC 地址。
 */
using Company.Logger;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;

namespace Company.Hmi.Presentation;

public partial class CylinderParameterView : UserControl
{
    public event Action<string>? ParameterPageRequested;

    private void ParameterPageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } && page != "current")
            ParameterPageRequested?.Invoke(page);
    }

    private Window? _window;
    private bool _parameterDialogOpen;
    public CylinderParameterView()
    {
        InitializeComponent();
        // DataGridColumn 不继承页面 DataContext，显式绑定可用信号决定是否显示报警列。
        BindingOperations.SetBinding(CylinderAlarmColumn, DataGridColumn.VisibilityProperty,
            new Binding("DataContext." + nameof(ICylinderParameterPage.HasAlarmFeedback))
            { Source = this, Converter = new BooleanToVisibilityConverter(), FallbackValue = Visibility.Collapsed });
        Loaded += (_, _) => { Detach(); _window = Window.GetWindow(this); if (_window is not null) _window.StateChanged += WindowStateChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { Detach(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    private void Detach() { if (_window is not null) _window.StateChanged -= WindowStateChanged; _window = null; }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    private async void UpdateVisibility()
    {
        try { if (DataContext is ICylinderParameterPage vm) await vm.SetViewVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private void PreviousComponentClicked(object sender, RoutedEventArgs e) => SelectComponent(-1);
    private void NextComponentClicked(object sender, RoutedEventArgs e) => SelectComponent(1);
    private void SelectComponent(int offset)
    {
        if (DataContext is ICylinderParameterPage vm) vm.SelectComponent(offset);
    }
    private async void OperationClicked(object sender, RoutedEventArgs e)
    { if (DataContext is ICylinderParameterPage vm && sender is Button { Tag: string action }) await vm.OperateAsync(action); }
    private async void ParameterPressed(object sender, MouseButtonEventArgs e)
    { e.Handled = true; if (sender is TextBox { DataContext: { } field }) await EditParameterAsync(field); }
    private async void ParameterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        if (!e.IsRepeat && sender is TextBox { DataContext: { } field }) await EditParameterAsync(field);
    }
    private async Task EditParameterAsync(object field)
    {
        if (_parameterDialogOpen || DataContext is not ICylinderParameterPage vm) return;
        _parameterDialogOpen = true;
        try
        {
            if (vm.BeginEntry(field) is not { } entry) return;
            var dialog = new ParameterEntryDialog(entry) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) await entry.CommitAsync(dialog.InputValue);
        }
        catch (Exception ex) { Logs.LogError(ex); }
        finally { _parameterDialogOpen = false; }
    }
}

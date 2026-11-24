using Company.Hmi.Contracts;
using Company.Logger;
using System.Windows;
using System.Windows.Controls;

namespace Company.Hmi.Presentation;

public partial class IoMonitorView : UserControl
{
    private Window? _window;
    private IIoMonitor? _monitor;

    public IoMonitorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += WindowStateChanged;
            UpdateVisibility();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= WindowStateChanged;
            _window = null;
            UpdateVisibility();
        };
        IsVisibleChanged += (_, _) => UpdateVisibility();
        DataContextChanged += (_, _) => UpdateVisibility();
    }

    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();

    private async void UpdateVisibility()
    {
        try
        {
            var previous = _monitor;
            _monitor = DataContext as IIoMonitor;
            // 调用前一次可见性变更立即交给对应适配器，其排队逻辑负责拒绝过时代次。
            var next = _monitor;
            bool visible = IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized;
            if (previous is not null && !ReferenceEquals(previous, next))
            {
                var hide = previous.SetViewVisibleAsync(false);
                var show = next?.SetViewVisibleAsync(visible) ?? Task.CompletedTask;
                await Task.WhenAll(hide, show);
            }
            else if (next is not null)
                await next.SetViewVisibleAsync(visible);
        }
        catch (Exception exception)
        {
            Logs.LogError(exception);
        }
    }
}

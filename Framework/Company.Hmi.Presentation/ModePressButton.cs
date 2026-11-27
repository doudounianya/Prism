using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.CompilerServices;

namespace Company.Hmi.Presentation;

/// <summary>初始化按钮的按住交互。鼠标、触摸与键盘统一发出一次按下和一次释放事件，
/// 不访问 PLC；触摸事件处理后不再产生重复的鼠标按下。丢失捕获、焦点或离页都视为释放。</summary>
public static class ModePressButton
{
    private sealed class Hold
    {
        public bool Active;
        public TouchDevice? Touch;
        public Key? Key;
    }
    private static readonly ConditionalWeakTable<Button, Hold> Holds = new();
    private static Hold State(Button button) => Holds.GetValue(button, _ => new Hold());
    public static bool IsHeld(Button button) => State(button).Active;
    public static void CancelAll(DependencyObject root)
    {
        if (root is Button button && GetUseHold(button)) Release(button);
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) CancelAll(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    public static readonly DependencyProperty UseHoldProperty = DependencyProperty.RegisterAttached("UseHold", typeof(bool), typeof(ModePressButton), new PropertyMetadata(false, Changed));
    public static bool GetUseHold(DependencyObject element) => (bool)element.GetValue(UseHoldProperty);
    public static void SetUseHold(DependencyObject element, bool value) => element.SetValue(UseHoldProperty, value);
    public static readonly RoutedEvent PressedEvent = EventManager.RegisterRoutedEvent("Pressed", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ModePressButton));
    public static readonly RoutedEvent ReleasedEvent = EventManager.RegisterRoutedEvent("Released", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ModePressButton));
    public static void AddPressedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).AddHandler(PressedEvent, handler);
    public static void RemovePressedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).RemoveHandler(PressedEvent, handler);
    public static void AddReleasedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).AddHandler(ReleasedEvent, handler);
    public static void RemoveReleasedHandler(DependencyObject element, RoutedEventHandler handler) => ((UIElement)element).RemoveHandler(ReleasedEvent, handler);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Button button) return;
        button.PreviewMouseLeftButtonDown -= MouseDown; button.PreviewMouseLeftButtonUp -= MouseUp;
        button.PreviewTouchDown -= TouchDown; button.PreviewTouchUp -= TouchUp;
        button.LostMouseCapture -= LostMouse; button.LostTouchCapture -= LostTouch;
        button.PreviewKeyDown -= KeyDown; button.PreviewKeyUp -= KeyUp;
        button.LostKeyboardFocus -= LostKeyboard; button.Unloaded -= Unloaded; button.IsEnabledChanged -= EnabledChanged; button.IsVisibleChanged -= VisibleChanged;
        if (!(bool)args.NewValue) { Release(button); return; }
        button.PreviewMouseLeftButtonDown += MouseDown; button.PreviewMouseLeftButtonUp += MouseUp;
        button.PreviewTouchDown += TouchDown; button.PreviewTouchUp += TouchUp;
        button.LostMouseCapture += LostMouse; button.LostTouchCapture += LostTouch;
        button.PreviewKeyDown += KeyDown; button.PreviewKeyUp += KeyUp;
        button.LostKeyboardFocus += LostKeyboard; button.Unloaded += Unloaded; button.IsEnabledChanged += EnabledChanged; button.IsVisibleChanged += VisibleChanged;
    }
    private static void Begin(Button button)
    {
        var hold = State(button);
        if (hold.Active || !button.IsEnabled) return;
        hold.Active = true; button.RaiseEvent(new RoutedEventArgs(PressedEvent, button));
    }
    internal static void Release(Button button)
    {
        var hold = State(button);
        if (!hold.Active) return;
        // 先清除本地状态，再交还捕获，LostCapture 的重入不会再发送第二次 False。
        hold.Active = false;
        var touch = hold.Touch; hold.Touch = null; hold.Key = null;
        if (touch is not null) button.ReleaseTouchCapture(touch);
        if (button.IsMouseCaptured) button.ReleaseMouseCapture();
        button.RaiseEvent(new RoutedEventArgs(ReleasedEvent, button));
    }
    private static void MouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = (Button)sender; e.Handled = true;
        if (State(button).Active || !button.IsEnabled || !button.CaptureMouse()) return;
        Begin(button);
    }
    private static void MouseUp(object sender, MouseButtonEventArgs e)
    {
        var button = (Button)sender; e.Handled = true;
        var hold = State(button);
        if (hold.Touch is null && hold.Key is null) Release(button);
    }
    private static void TouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is not Button button) return; e.Handled = true;
        var hold = State(button);
        if (hold.Active || !button.IsEnabled || !button.CaptureTouch(e.TouchDevice)) return;
        hold.Touch = e.TouchDevice; Begin(button);
    }
    private static void TouchUp(object? sender, TouchEventArgs e)
    {
        if (sender is not Button button) return; e.Handled = true;
        if (State(button).Touch?.Id == e.TouchDevice.Id) Release(button);
    }
    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true; var button = (Button)sender; var hold = State(button);
        if (e.IsRepeat || hold.Active || !button.IsEnabled) return;
        hold.Key = e.Key; Begin(button);
    }
    private static void KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true; var button = (Button)sender;
        if (State(button).Key == e.Key) Release(button);
    }
    private static void LostMouse(object sender, MouseEventArgs e)
    { var button = (Button)sender; var hold = State(button); if (hold.Touch is null && hold.Key is null) Release(button); }
    private static void LostTouch(object? sender, TouchEventArgs e)
    { if (sender is Button button && State(button).Touch?.Id == e.TouchDevice.Id) Release(button); }
    private static void LostKeyboard(object sender, KeyboardFocusChangedEventArgs e) => Release((Button)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Release((Button)sender);
    private static void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Release((Button)sender); }
    private static void VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Release((Button)sender); }
}


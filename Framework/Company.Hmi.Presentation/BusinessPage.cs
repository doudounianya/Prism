using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Company.Hmi.Presentation;

/// <summary>共用页面只报告导航意图和真实可见性，宿主负责权限、查询和数据来源。</summary>
public class BusinessPage : UserControl
{
    public static readonly DependencyProperty PageTargetProperty = DependencyProperty.RegisterAttached(
        "PageTarget", typeof(string), typeof(BusinessPage), new PropertyMetadata(null));

    public static string? GetPageTarget(DependencyObject item) => (string?)item.GetValue(PageTargetProperty);
    public static void SetPageTarget(DependencyObject item, string value) => item.SetValue(PageTargetProperty, value);

    public event Action<string>? PageRequested;
    public event Action<bool>? PageVisibilityChanged;

    public BusinessPage()
    {
        Loaded += (_, _) => PublishVisibility();
        IsVisibleChanged += (_, _) => PublishVisibility();
        DataContextChanged += (_, _) => PublishVisibility();
        Unloaded += (_, _) => PageVisibilityChanged?.Invoke(false);
        AddHandler(Button.ClickEvent, new RoutedEventHandler(Navigate));
    }

    private void PublishVisibility() => PageVisibilityChanged?.Invoke(IsLoaded && IsVisible);

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button button && button.Tag as string != "current" && GetPageTarget(button) is { } target)
            PageRequested?.Invoke(target);
    }
}

public sealed class TrendWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Max(1, value is int count ? count : 0) * 65d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>将页面上下文交给不属于可视树的 DataGridColumn。</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));
    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

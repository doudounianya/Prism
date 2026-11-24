using System.Windows;
using System.Windows.Controls;

namespace Company.Hmi.Presentation;

public partial class SettingsPage : UserControl
{
    public static readonly DependencyProperty SoftwareContentProperty =
        DependencyProperty.Register(nameof(SoftwareContent), typeof(object), typeof(SettingsPage));
    public static readonly DependencyProperty AccountContentProperty =
        DependencyProperty.Register(nameof(AccountContent), typeof(object), typeof(SettingsPage));
    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(SettingsPage));

    public object? SoftwareContent { get => GetValue(SoftwareContentProperty); set => SetValue(SoftwareContentProperty, value); }
    public object? AccountContent { get => GetValue(AccountContentProperty); set => SetValue(AccountContentProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public SettingsPage() => InitializeComponent();
}

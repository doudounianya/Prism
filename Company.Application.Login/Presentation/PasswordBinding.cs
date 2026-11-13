using System.Windows;
using System.Windows.Controls;

namespace Company.Application.Login.Presentation;

/// <summary>
/// 用附加依赖属性桥接 PasswordBox.Password 与视图模型，默认启用双向绑定。
/// 仅同步账号弹窗的密码输入，不在此处执行认证，也不把密码写入提示或日志。
/// </summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string),
        typeof(PasswordBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    /// <summary>供 XAML 附加属性机制读取当前绑定值；空值统一表现为空字符串。</summary>
    public static string GetPassword(DependencyObject target) => (string?)target.GetValue(PasswordProperty) ?? "";
    /// <summary>供 XAML 附加属性机制设置密码值，并由属性回调同步到 PasswordBox。</summary>
    public static void SetPassword(DependencyObject target, string value) => target.SetValue(PasswordProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not PasswordBox box) return;
        // 先断开输入事件再设置控件，避免模型到视图的同步立即反向写回造成递归。
        box.PasswordChanged -= PasswordChanged;
        var password = e.NewValue as string ?? "";
        if (box.Password != password) box.Password = password;
        box.PasswordChanged += PasswordChanged;
    }
    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        // 更新依赖属性当前值而保留原有绑定表达式，让用户输入沿双向绑定回到模型。
        box.SetCurrentValue(PasswordProperty, box.Password);
    }
}

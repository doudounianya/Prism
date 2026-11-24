using System.Windows.Controls;
namespace Company.PlcHmi.Login.Views;
/// <summary>本人改密弹窗的视图；代码后置只初始化 XAML，原密码验证由绑定的视图模型和服务处理。</summary>
public partial class ChangePasswordView : UserControl
{
    /// <summary>实例化声明于 XAML 的控件和绑定。</summary>
    public ChangePasswordView() => InitializeComponent();
}

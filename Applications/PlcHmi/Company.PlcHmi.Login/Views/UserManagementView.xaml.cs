using System.Windows.Controls;
namespace Company.PlcHmi.Login.Views;
/// <summary>管理员用户维护弹窗的视图；代码后置只初始化 XAML，列表及管理命令由绑定提供。</summary>
public partial class UserManagementView : UserControl
{
    /// <summary>实例化声明于 XAML 的控件和绑定。</summary>
    public UserManagementView() => InitializeComponent();
}

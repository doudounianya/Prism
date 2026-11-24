using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.Login;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class ModeControlView : UserControl
{
    public ModeControlView()
    {
        InitializeComponent();
        Shared.MaintenanceVerificationRequested += ShowMaintenanceVerification;
    }

    /// <summary>维护专用凭据验证窗口。只把密码交给既有账号服务，关闭及提交后立即清空；
    /// 不修改登录会话。最后一人额外提示退出结果需由 PLC 确认，失败保留锁供本人重试。</summary>
    private void ShowMaintenanceVerification(object? row, bool administratorAll)
    {
        var person = row as MaintenanceInterlock.Person;
        if (DataContext is not ModeControlViewModel vm) return;
        var owner = Window.GetWindow(this);
        var dialog = new Window { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = administratorAll ? "管理员解除全部维护锁" : person is null ? "人员上锁" : "本人解锁", Width = 500, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontSize = 18 };
        var panel = new StackPanel { Margin = new Thickness(26) };
        void Label(string value) => panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });
        Label(administratorAll ? "此操作将解除所有人员的维护锁，必须重新输入管理员账户和密码。" : person is null ? "输入本人账户，上锁不会切换当前登录用户。" : "只能验证上锁者本人账户，解除本人的人员锁。");
        Label("用户名");
        var name = new TextBox { Text = person?.UserName ?? "", IsReadOnly = person is not null, MinHeight = 44, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(name); Label("密码");
        var password = new PasswordBox { MinHeight = 44, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(password);
        if (administratorAll) Label("确认解除全部锁后将申请退出维护。管理员身份、全部持锁人员及操作结果会写入操作日志；PLC 未确认退出前保持设备操作禁用。");
        if (person is not null && MaintenanceInterlock.People.Length == 1)
            Label("这是最后一把人员锁。验证后申请退出维护，PLC 确认退出前设备操作继续禁用，不会自动运行设备。");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", MinWidth = 96, MinHeight = 44, Margin = new Thickness(0, 0, 12, 0) };
        var accept = new Button { Content = administratorAll ? "管理员确认全部解锁" : person is null ? "验证并上锁" : "验证并解锁", MinWidth = 150, MinHeight = 44, IsDefault = !administratorAll };
        bool submitting = false;
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, args) => { if (submitting) args.Cancel = true; };
        dialog.Closed += (_, _) => password.Clear();
        accept.Click += async (_, _) =>
        {
            if (submitting) return;
            submitting = true; accept.IsEnabled = false; cancel.IsEnabled = false; message.Text = "正在验证身份并核对维护请求…";
            try { await vm.MaintainAsync(name.Text, password.Password, person?.Id, administratorAll); submitting = false; dialog.Close(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { password.Clear(); submitting = false; accept.IsEnabled = true; cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.Loaded += (_, _) => { if (person is null) name.Focus(); else password.Focus(); };
        dialog.ShowDialog();
    }
}

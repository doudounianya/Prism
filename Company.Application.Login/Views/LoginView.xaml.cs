using Company.Core.Ioc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Company.Application.Login.Views
{
    /// <summary>
    /// 启动登录页的视图后置代码，初始化控件并把三种语言的单选事件转交语言管理器。
    /// 账号认证及会话切换由绑定的登录视图模型负责。
    /// </summary>
    public partial class LoginView : UserControl
    {
        /// <summary>初始化 XAML 后订阅语言选项；同一视图实例只建立一次事件连接。</summary>
        public LoginView()
        {
            InitializeComponent();

            // 子控件与视图生命周期一致，只在构造时订阅一次。
            CN.Checked += Checked;
            TW.Checked += Checked;
            US.Checked += Checked;
        }

        /// <summary>无标题栏时保留关闭入口，不绕过宿主已有的托盘/取消/关闭确认流程。</summary>
        private void CloseLoginClicked(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

        /// <summary>标题及空白处可拖动窗口；输入框、按钮和滚动部件的交互不触发拖动。</summary>
        private void DragBlankArea(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, this);)
            {
                if (source is System.Windows.Controls.Primitives.ButtonBase or
                    System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or
                    System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            if (Window.GetWindow(this) is not { } window) return;
            try { window.DragMove(); e.Handled = true; }
            catch (InvalidOperationException) { /* 鼠标已松开时不再启动系统拖动。 */ }
        }


        // 以控件名称映射语言枚举；事件不是 RadioButton 或名称不匹配时不执行切换。
        private void Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton button)
            {
                switch (button.Name)
                {
                    case "CN":
                        PrismProvider.LanguageManager.Set(Core.Enums.LanguageType.CN);
                        break;
                    case "TW":
                        PrismProvider.LanguageManager.Set(Core.Enums.LanguageType.TW);
                        break;
                    case "US":
                        PrismProvider.LanguageManager.Set(Core.Enums.LanguageType.US);
                        break;
                    default:
                        break;
                }
            }

        }
    }
}

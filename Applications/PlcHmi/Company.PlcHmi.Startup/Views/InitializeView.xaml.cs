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

namespace Company.PlcHmi.Startup.Views
{
    /// <summary>
    /// 启动准备页的视图后置代码，仅创建 XAML 控件树。
    /// 状态文字及加载命令由绑定的视图模型提供，构造函数不直接启动运行时。
    /// </summary>
    public partial class InitializeView : UserControl
    {
        /// <summary>初始化准备页的控件与 XAML 绑定。</summary>
        public InitializeView()
        {
            InitializeComponent();
        }
    }
}

/*
 * 文件职责与维护说明：
 * 主视图将整个主界面的加载/显隐状态交给 MainViewModel，仅在主界面真正显示时启动界面状态刷新。
 * 工作区继续保留已访问页面，后续导航无需重复装卸。
 */
using System.Windows.Controls;
using System.Windows;
using Company.Application.Main.ViewModels;

namespace Company.Application.Main.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateVisibility();
            }));
            Unloaded += (_, _) => { if (DataContext is MainViewModel vm) vm.Deactivate(); };
            IsVisibleChanged += (_, _) => UpdateVisibility();
        }
        private void UpdateVisibility()
        {
            if (DataContext is not MainViewModel vm) return;
            if (IsLoaded && IsVisible) vm.Activate(); else vm.Deactivate();
        }

        /// <summary>只准备本地显示行和模板；不调用导航、可见会话、数据库查询或 PLC 读写。</summary>
        public void WarmWorkspacePage(FrameworkElement page)
        {
            switch (page.DataContext)
            {
                case IoMonitorViewModel io: io.PrepareDisplay(); break;
                case ComponentParameterViewModel component: component.PrepareDisplay(); break;
                case ProcessParameterViewModel process: process.PrepareDisplay(); break;
                case RealtimeAlarmViewModel alarm: alarm.Refresh(); break;
            }
            Workspace.ApplyTemplate();
            Workspace.WarmPage(page);
        }

        /// <summary>页面仍留在原工作区，只收起预热时参与布局的承载层。</summary>
        public void FinishWorkspacePage(FrameworkElement page) => Workspace.FinishWarmup(page);
    }
}

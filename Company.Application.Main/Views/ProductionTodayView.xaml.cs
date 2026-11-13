/*
 * 文件职责与维护说明：
 * 今日产量视图转发显示状态，使视图模型按需订阅缓存变化。
 * 离页只停止页面更新，后台产能采集、确认与持久化继续由独立服务管理。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;

public partial class ProductionTodayView : UserControl
{
    public ProductionTodayView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is ProductionTodayViewModel vm) vm.SetVisible(IsVisible); };
        IsVisibleChanged += (_, _) => { if (DataContext is ProductionTodayViewModel vm) vm.SetVisible(IsVisible); };
        Unloaded += (_, _) => { if (DataContext is ProductionTodayViewModel vm) vm.SetVisible(false); };
    }
}

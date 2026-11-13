/*
 * 文件职责与维护说明：
 * 产量趋势视图负责页面生命周期与图形交互事件的转交。
 * 柱形选择与打开日报使用视图模型中的成功查询上下文，不在视图中重新计算产量。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;

public partial class ProductionTrendView : UserControl
{
    public ProductionTrendView()
    {
        InitializeComponent();
        // 为较小窗口保留可滚动的完整内容，大窗口仍按可用高度铺满。
        SizeChanged += (_, _) => PageLayout.Height = Math.Max(700, ActualHeight - 28);
        Loaded += (_, _) => { if (DataContext is ProductionTrendViewModel vm) vm.SetVisible(IsVisible); };
        IsVisibleChanged += (_, _) => { if (DataContext is ProductionTrendViewModel vm) vm.SetVisible(IsVisible); };
        Unloaded += (_, _) => { if (DataContext is ProductionTrendViewModel vm) vm.SetVisible(false); };
    }
}

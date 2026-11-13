/*
 * 文件职责与维护说明：
 * 生产日报视图只转发页面生命周期，数据来源、生产日期及查询取消由共用视图模型管理。
 * 保留视图实例可避免切回页面时重建布局。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;

public partial class ProductionDailyView : UserControl
{
    public ProductionDailyView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is ProductionDailyViewModel vm) vm.SetVisible(IsVisible); };
        IsVisibleChanged += (_, _) => { if (DataContext is ProductionDailyViewModel vm) vm.SetVisible(IsVisible); };
        Unloaded += (_, _) => { if (DataContext is ProductionDailyViewModel vm) vm.SetVisible(false); };
    }
}

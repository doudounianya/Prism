/*
 * 文件职责与维护说明：
 * 历史报警视图负责通知页面是否可见，查询、分页及取消逻辑位于视图模型中。
 * 视图对象被保留不代表隐藏期间继续发起查询。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;

public partial class AlarmHistoryView : UserControl
{
    public AlarmHistoryView()
    {
        InitializeComponent();
        Loaded += (_,_) => {if(DataContext is AlarmHistoryViewModel vm)vm.SetVisible(IsVisible);};
        IsVisibleChanged += (_,_) => {if(DataContext is AlarmHistoryViewModel vm)vm.SetVisible(IsVisible);};
        Unloaded += (_,_) => {if(DataContext is AlarmHistoryViewModel vm)vm.SetVisible(false);};
    }
}

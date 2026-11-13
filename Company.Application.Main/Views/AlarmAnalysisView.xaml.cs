/*
 * 文件职责与维护说明：
 * 报警分析视图把 Loaded、Unloaded 和可见性变化转交视图模型，保持单例页面重复显示时的查询生命周期。
 * 统计与数据库查询由视图模型/服务负责。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;

public partial class AlarmAnalysisView : UserControl
{
    public AlarmAnalysisView()
    {
        InitializeComponent();
        Loaded += (_,_) => {if(DataContext is AlarmAnalysisViewModel vm)vm.SetVisible(IsVisible);};
        IsVisibleChanged += (_,_) => {if(DataContext is AlarmAnalysisViewModel vm)vm.SetVisible(IsVisible);};
        Unloaded += (_,_) => {if(DataContext is AlarmAnalysisViewModel vm)vm.SetVisible(false);};
    }
}

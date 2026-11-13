/*
 * 文件职责与维护说明：
 * 实时报警视图转交可见性变化，使页面只在显示时刷新内存快照。
 * 后台报警消费者与历史保存不依赖这个视图是否显示。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;

namespace Company.Application.Main.Views;
public partial class RealtimeAlarmView : UserControl
{
    public RealtimeAlarmView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (DataContext is RealtimeAlarmViewModel vm) vm.SetVisible(IsVisible); };
        Unloaded += (_, _) => { if (DataContext is RealtimeAlarmViewModel vm) vm.SetVisible(false); };
    }
}

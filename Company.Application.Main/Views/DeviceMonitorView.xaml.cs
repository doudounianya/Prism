/*
 * 文件职责与维护说明：
 * 设备监控视图仅将实际加载与显示状态转交视图模型，控制 UI 状态快照刷新。
 * 该视图不会创建 PLC 连接或改变后台采样。
 */
using Company.Application.Main.ViewModels;
using System.Windows.Controls;
namespace Company.Application.Main.Views;
public partial class DeviceMonitorView : UserControl
{
    public DeviceMonitorView()
    {
        InitializeComponent();
        Loaded += (_, _) => Update(); Unloaded += (_, _) => Update(); IsVisibleChanged += (_, _) => Update();
    }
    private void Update() { if (DataContext is DeviceMonitorViewModel vm) vm.SetVisible(IsLoaded && IsVisible); }
}

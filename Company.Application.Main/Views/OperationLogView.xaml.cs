using Company.Application.Main.ViewModels;
using System.Windows.Controls;

namespace Company.Application.Main.Views;

/// <summary>将常驻视图的真实可见性传给日志查询；隐藏时停止等待，不启动任何设备通讯。</summary>
public partial class OperationLogView : UserControl
{
    public OperationLogView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is OperationLogViewModel vm) vm.SetVisible(IsVisible); };
        IsVisibleChanged += (_, _) => { if (DataContext is OperationLogViewModel vm) vm.SetVisible(IsVisible); };
        Unloaded += (_, _) => { if (DataContext is OperationLogViewModel vm) vm.SetVisible(false); };
    }
}

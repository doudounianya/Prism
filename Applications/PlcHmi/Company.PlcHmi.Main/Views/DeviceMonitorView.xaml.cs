using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class DeviceMonitorView : UserControl
{
    public DeviceMonitorView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is DeviceMonitorViewModel model) model.SetVisible(visible);
        };
    }
}

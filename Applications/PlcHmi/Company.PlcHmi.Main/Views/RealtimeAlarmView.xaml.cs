using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class RealtimeAlarmView : UserControl
{
    public RealtimeAlarmView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is RealtimeAlarmViewModel model) model.SetVisible(visible);
        };
        Page.PageRequested += PreviewNavigation.Request;
    }
}

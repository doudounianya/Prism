using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class AlarmHistoryView : UserControl
{
    public AlarmHistoryView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is AlarmHistoryViewModel model) model.SetVisible(visible);
        };
        Page.PageRequested += PreviewNavigation.Request;
    }
}

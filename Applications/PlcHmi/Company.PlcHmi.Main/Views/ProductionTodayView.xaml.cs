using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class ProductionTodayView : UserControl
{
    public ProductionTodayView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is ProductionTodayViewModel model) model.SetVisible(visible);
        };
        Page.PageRequested += PreviewNavigation.Request;
    }
}

using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class ProductionDailyView : UserControl
{
    public ProductionDailyView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is ProductionDailyViewModel model) model.SetVisible(visible);
        };
        Page.PageRequested += PreviewNavigation.Request;
    }
}

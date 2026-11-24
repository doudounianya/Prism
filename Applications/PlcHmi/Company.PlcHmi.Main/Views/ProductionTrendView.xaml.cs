using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class ProductionTrendView : UserControl
{
    public ProductionTrendView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is ProductionTrendViewModel model) model.SetVisible(visible);
        };
        Page.PageRequested += PreviewNavigation.Request;
    }
}

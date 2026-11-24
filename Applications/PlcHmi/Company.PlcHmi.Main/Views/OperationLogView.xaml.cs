using Company.PlcHmi.Main.ViewModels;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class OperationLogView : UserControl
{
    public OperationLogView()
    {
        InitializeComponent();
        Page.PageVisibilityChanged += visible =>
        {
            if (DataContext is OperationLogViewModel model) model.SetVisible(visible);
        };
    }
}

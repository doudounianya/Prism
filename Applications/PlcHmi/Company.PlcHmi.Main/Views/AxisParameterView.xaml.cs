using Company.PlcHmi.Main.Presentation;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class AxisParameterView : UserControl
{
    public AxisParameterView()
    {
        InitializeComponent();
        Shared.ParameterPageRequested += PreviewNavigation.Request;
    }

}

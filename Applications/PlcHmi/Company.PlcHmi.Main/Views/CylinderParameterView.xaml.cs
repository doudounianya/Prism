using Company.PlcHmi.Main.Presentation;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class CylinderParameterView : UserControl
{
    public CylinderParameterView()
    {
        InitializeComponent();
        Shared.ParameterPageRequested += PreviewNavigation.Request;
    }

}

namespace Company.Hmi.Presentation;

public partial class ProductionTrendView : BusinessPage
{
    public ProductionTrendView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PageLayout.Height = Math.Max(700, ActualHeight - 28);
    }
}

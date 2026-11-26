#if FLYSCAN
using ProductApplication = Company.FlyScan.Shell.App;
#else
using ProductApplication = Company.PlcHmi.Shell.App;
#endif

namespace Company.Shell;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new ProductApplication();
        application.InitializeComponent();
        return application.Run();
    }
}

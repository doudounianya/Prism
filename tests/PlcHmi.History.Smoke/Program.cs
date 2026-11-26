using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Company.Core.Helper;
using Company.Core.Ioc;
using Company.Database.Accounts;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Configuration.Services.PLC;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.Prism;
using MahApps.Metro.Controls.Dialogs;
using Prism.Ioc;

internal static partial class Program
{
    private const string UserName = "prism-offline-fixture";
    private static readonly DateTime ProductionDate = new(2000, 1, 3);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string root = AppContext.BaseDirectory;
        if (args is not ["seed" or "read" or "opcua" or "parameters"] || !File.Exists(Path.Combine(root, "history-fixture.json")))
            return 2;
        if (Mutex.TryOpenExisting(@"Local\CRT_HMI.SingleInstance", out var existing))
        {
            existing.Dispose();
            Console.Error.WriteLine("Another PLC HMI instance is running in this Windows session.");
            return 2;
        }
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(root, "history-fixture.json")))!;
        using var database = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Config", "Database.local.json")));
        var config = database.RootElement;
        var workbook = Path.Combine(root, "Config", "PLC", "CRT_HMI_PLC变量绑定配置.xlsx");
        Check(config.GetProperty("Provider").GetString() == "MySql" &&
            Regex.IsMatch(config.GetProperty("ConnectionString").GetString()!,
                @"\AServer=127\.0\.0\.1;Port=\d{1,5};Database=prism_fixture(?:_restored)?;User Id=prism_fixture;Password=[a-f0-9]{32};\z") &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(workbook))) == fixture.WorkbookSha256,
            "isolated database and fixture workbook verified before startup");
        Environment.SetEnvironmentVariable("PRISM_DATABASE_PROVIDER", null);
        Environment.SetEnvironmentVariable("PRISM_DATABASE_CONNECTION_STRING", null);
        string mode = args[0];
        bool loopback = mode is "opcua" or "parameters";
        if (loopback != fixture.OpcUaPort.HasValue) return 2;
        using var opcua = fixture.OpcUaPort is { } port ? new LoopbackOpcUa(port, mode == "parameters") : null;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(root, "Modules", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        if (loopback)
            Check(typeof(Company.PlcHmi.Shell.App).Assembly.EntryPoint?.DeclaringType?.FullName == "Company.Shell.Program",
                "PLC HMI executable uses the common shell entry point");
        var app = new Company.PlcHmi.Shell.App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Verify(app, fixture.AccountSecret, mode, opcua); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        }, DispatcherPriority.ApplicationIdle);
        return app.Run();
    }

    private static async Task Verify(Company.PlcHmi.Shell.App app, string secret, string mode, LoopbackOpcUa? opcua)
    {
        var window = (Company.PlcHmi.Shell.Views.MainWindow)app.MainWindow;
        var container = PrismProvider.Container;
        await Wait(() => Active(RegionNames.MainRegion)?.GetType().Name == ViewNames.LoginView, "packaged login view loaded");
        if (opcua is not null)
        {
            // The login shell has not loaded the configuration module yet. Preflight the file without starting any device.
            var preflight = new PlcBindingConfigProvider(new PlcBindingExcelLoader(), new PlcBindingValidator());
            await ValidateLoopbackWorkbook(preflight, opcua, mode == "parameters");
            await opcua.ConnectAsync();
            Console.WriteLine("PASS OPC UA fixture listening on IPv4 loopback only");
        }
        var session = container.Resolve<ISession>();
        if (mode == "parameters")
        {
            var accounts = container.Resolve<IUserAccountRepository>();
            await VerifyParameterPermissions(window, container, session, accounts, secret, opcua!.Nodes);
        }
        else
        {
            if (mode != "read")
                SeedAccount(container.Resolve<IUserAccountRepository>(), UserName, UserRole.Administrator, secret);
            await Login(session, UserName, UserRole.Administrator, secret);
            if (opcua is not null)
                await VerifyOpcUa(window, container.Resolve<IPlcRuntime>(), opcua);
            else
                await VerifyHistory(window, container);
            await Logout(window, session);
        }

        window.Close();
        MessageDialog? dialog = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            dialog = await window.GetCurrentDialogAsync<MessageDialog>();
            if (dialog is { IsLoaded: true }) break;
            await Task.Delay(40);
        }
        Check(dialog is { IsLoaded: true }, "close confirmation displayed");
        window.Closed += (_, _) => Console.WriteLine("PASS packaged window closed normally");
        Click(FindButton(dialog!, "关闭程序"));
    }

    private static void SeedAccount(IUserAccountRepository accounts, string userName, UserRole role, string secret) =>
        Check(accounts.Insert(new UserAccount { UserName = userName, Role = (int)role,
            PasswordHash = MD5Helper.GetMD5_32(secret), CreatedAt = DateTime.Now }), $"created synthetic account for {role}");

    private static async Task Login(ISession session, string userName, UserRole role, string secret)
    {
        await Wait(() => Active(RegionNames.MainRegion)?.GetType().Name == ViewNames.LoginView, "login screen available");
        var login = (FrameworkElement)Active(RegionNames.MainRegion)!;
        var button = (Button)login.FindName("LoginButton");
        await Wait(() => button.IsEnabled, "login command available");
        var userInput = (TextBox)login.FindName("UserNameInput");
        userInput.SetCurrentValue(TextBox.TextProperty, userName);
        userInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ((PasswordBox)login.FindName("PasswordInput")).Password = secret;
        Click(button);
        await Wait(() => Active(RegionNames.MainRegion)?.GetType().Name == ViewNames.MainView &&
            session.CurrentUser is { Id: > 0 } user && user.UserName == userName && user.Role == (int)role,
            $"synthetic {role} authenticated and sees the packaged workspace");
    }

    private static async Task Logout(Window window, ISession session)
    {
        var signOut = FindButton(window, "退出登录");
        var command = signOut.Command;
        Click(signOut);
        // Identity changes before the asynchronous sign-out has finished saving preferences.
        await Wait(() => session.CurrentUser.Id == 0 && command.CanExecute(signOut.CommandParameter) &&
            Active(RegionNames.MainRegion)?.GetType().Name == ViewNames.MainView,
            "logout removes the authenticated session and keeps the monitoring workspace open");
    }

    private static object? Active(string region) => PrismProvider.RegionManager.Regions[region].ActiveViews.FirstOrDefault();

    private static async Task<T> Navigate<T>(Window window, string caption, string view) where T : class
    {
        Click(FindButton(window, caption));
        await Wait(() => Active(RegionNames.WorkspaceRegion)?.GetType().Name == view, "navigation " + view);
        return (T)((FrameworkElement)Active(RegionNames.WorkspaceRegion)!).DataContext;
    }

    private static async Task VerifyHistory(Window window, IContainerProvider container)
    {
        var configuration = container.Resolve<IPlcBindingConfigProvider>().Current;
        Check(configuration is { Devices.Count: > 0 } && configuration.Devices.All(device => !device.Enabled) &&
            container.Resolve<IPlcRuntime>().Status.Devices.Count == 0 &&
            !container.Resolve<IUserAccessService>().CanEditParameters, "PLC devices disabled and parameter controls read-only");

        Click(FindButton(window, "报警监控"));
        await Wait(() => Active(RegionNames.WorkspaceRegion)?.GetType().Name == ViewNames.RealtimeAlarmView, "alarm workspace opens");
        var alarms = await Navigate<AlarmHistoryViewModel>(window, "历史报警", ViewNames.AlarmHistoryView);
        SetAlarmRange(alarms.Filter);
        await alarms.QueryAsync();
        Check(alarms.HasQuery && alarms.Items is [{ Description: "Synthetic release alarm", ObservedDurationMs: 60000 }],
            "history restores the original alarm and 60 s observed duration");
        var analysis = await Navigate<AlarmAnalysisViewModel>(window, "报警分析", ViewNames.AlarmAnalysisView);
        SetAlarmRange(analysis.Filter);
        await analysis.QueryAsync();
        Check(analysis.HasQuery && analysis.Items.Count == 1, "alarm analysis restores the same event");

        Click(FindButton(window, "生产数据"));
        await Wait(() => Active(RegionNames.WorkspaceRegion)?.GetType().Name == ViewNames.ProductionTodayView, "production workspace opens");
        var daily = await Navigate<ProductionDailyViewModel>(window, "生产日报", ViewNames.ProductionDailyView);
        daily.Filter.DeviceId = "PLC01";
        daily.Filter.Date = ProductionDate;
        await daily.QueryAsync();
        Check(daily.First == "58", "daily report restores 58 pieces");
        var trend = await Navigate<ProductionTrendViewModel>(window, "产量趋势", ViewNames.ProductionTrendView);
        trend.Filter.DeviceId = "PLC01";
        trend.Filter.FromDate = trend.Filter.ToDate = ProductionDate;
        await trend.QueryAsync();
        Check(trend.HasQuery && trend.Day == "41" && trend.Night == "17" && trend.Total == "58", "trend restores 41 day and 17 night pieces");

    }

    private static void SetAlarmRange(AlarmQueryFilter filter)
    {
        filter.DeviceId = "PLC01";
        filter.FromDate = ProductionDate.AddDays(-1);
        filter.ToDate = ProductionDate.AddDays(2);
    }

    private static async Task Wait(Func<bool> found, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!found() && clock.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(40);
        Check(found(), description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private static IEnumerable<T> Walk<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T value) yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var nested in Walk<T>(VisualTreeHelper.GetChild(element, index))) yield return nested;
    }

    private static Button FindButton(DependencyObject root, string label) =>
        Walk<Button>(root).First(button => button.IsVisible && Equals(button.Content, label));

    private static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private sealed record Fixture(string AccountSecret, string WorkbookSha256, int? OpcUaPort);
}

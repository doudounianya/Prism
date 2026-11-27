using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Company.Database.Accounts;
using Company.Hardware.PLC.Models;
using Company.Hmi.Presentation;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Shared.Prism;
using Prism.Ioc;

internal static partial class Program
{
    private static async Task VerifyModeOperations(Window window, IContainerProvider container, ISession session,
        string secret, FixtureNodes nodes)
    {
        var access = container.Resolve<IUserAccessService>();
        var data = container.Resolve<IPlcDataProvider>();
        var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        var diagram = CurrentModeDiagram();
        var debug = FindButton(diagram, "调试开关");
        var initialize = FindButton(diagram, "按住初始化");
        await Wait(() => mode.ConfirmedState == (0, 0) && debug.IsEnabled && initialize.IsEnabled &&
            mode.Display.Action(6) == false && mode.Display.Action(7) == false && mode.Display.Action(8) == false,
            "manual stop feedback enables debug and initialization");
        Check(!access.CanEditParameters && nodes.ModeWrites().Length == 0, "startup in manual stop grants read access only and sends no commands");

        await VerifyDebugSwitch(mode, debug, debug, access, data, nodes, "the feedback panel");
        var diagramDebugEnter = Walk<Button>(diagram).Single(button => Equals(button.Tag, "DebugEnter"));
        var diagramDebugExit = Walk<Button>(diagram).Single(button => Equals(button.Tag, "DebugExit"));
        await VerifyDebugSwitch(mode, diagramDebugEnter, diagramDebugExit, access, data, nodes, "the manual mode diagram");
        await VerifyInitialization(mode, initialize, data, nodes, "the feedback panel");
        var diagramInitialize = Walk<Button>(diagram).Single(button => Equals(button.Tag, "Initialize"));
        await VerifyInitialization(mode, diagramInitialize, data, nodes, "the manual mode diagram");
        await VerifyPersonnelLocks(window, container, session, mode, secret, nodes);
        Check(nodes.ModeWrites().SequenceEqual([(6, true), (6, false), (6, true), (6, false),
                (7, true), (7, false), (7, true), (7, false),
                (8, true), (8, false), (8, true), (8, false)]) &&
            nodes.ActionBank().All(value => !value) && nodes.ParameterWrites().Length == 0 && nodes.WriteTrace().Rejected == 0,
            "the fixture saw exactly the expected mode writes and all action bits ended clear");
    }

    private static async Task VerifyDebugSwitch(ModeControlViewModel mode, Button enter, Button exit, IUserAccessService access,
        IPlcDataProvider data, FixtureNodes nodes, string location)
    {
        int writesBefore = nodes.ModeWrites().Length;
        await Wait(() => enter.IsVisible && enter.IsEnabled && mode.ConfirmedState == (0, 0) &&
            ReadBool(data, "Action[6]", false) && !access.CanEditParameters, $"debug can be requested in {location} from manual stop");
        Click(enter);
        await Wait(() => mode.Display.Action(6) == true && ReadBool(data, "Action[6]", true) &&
            nodes.ModeWrites().Skip(writesBefore).SequenceEqual([(6, true)]),
            $"the debug control in {location} writes and reads back b_Action[6]=True once");
        Check(mode.ConfirmedState == (0, 0) && !access.CanEditParameters,
            "a debug request acknowledgment alone keeps the PLC state stopped and parameters read-only");

        nodes.SetMode(0, 30);
        await Wait(() => mode.ConfirmedState == (0, 30) && access.CanEditParameters && exit.IsVisible && exit.IsEnabled,
            "PLC confirmation of manual debug opens parameter write access");
        Click(exit);
        await Wait(() => mode.Display.Action(6) == false && ReadBool(data, "Action[6]", false) &&
            nodes.ModeWrites().Skip(writesBefore).SequenceEqual([(6, true), (6, false)]) && !access.CanEditParameters,
            $"the debug control in {location} writes and reads back b_Action[6]=False once and revokes parameter write access");
        Check(mode.ConfirmedState == (0, 30) && !enter.IsEnabled && !exit.IsEnabled,
            "clearing the request leaves the PLC state at debug until feedback changes");

        nodes.SetMode(0, 0);
        await Wait(() => mode.ConfirmedState == (0, 0) && enter.IsEnabled && !access.CanEditParameters,
            "PLC stop feedback confirms exit from debug");
    }

    private static async Task VerifyInitialization(ModeControlViewModel mode, Button initialize, IPlcDataProvider data,
        FixtureNodes nodes, string location)
    {
        int writesBefore = nodes.ModeWrites().Length;
        Check(ModePressButton.GetUseHold(initialize), $"the initialization button in {location} uses hold interaction");
        nodes.SetMode(0, 70);
        await Wait(() => mode.ConfirmedState == (0, 70) && ReadBool(data, "Signal[5]", true) && !initialize.IsEnabled,
            "emergency feedback disables initialization");
        PressKey(initialize, Keyboard.PreviewKeyDownEvent);
        PressKey(initialize, Keyboard.PreviewKeyUpEvent);
        Check(nodes.ModeWrites().Length == writesBefore, "a disabled initialization button sends no request");

        nodes.SetMode(0, 0);
        await Wait(() => mode.ConfirmedState == (0, 0) && initialize.IsVisible && initialize.IsEnabled,
            $"returning to manual stop enables initialization in {location}");
        PressKey(initialize, Keyboard.PreviewKeyDownEvent);
        await Wait(() => ModePressButton.IsHeld(initialize) && mode.IsInitializationHeld &&
            ReadBool(data, "Action[7]", true) && nodes.ModeWrites().Skip(writesBefore).SequenceEqual([(7, true)]),
            "initialization press writes b_Action[7]=True once and reads it back");
        Check(mode.ConfirmedState == (0, 0), "initialization press does not advance the PLC state");
        nodes.SetMode(0, 40);
        await Wait(() => mode.ConfirmedState == (0, 40) && ModePressButton.IsHeld(initialize) && mode.IsInitializationHeld,
            "PLC confirms initialization while the button is held");
        PressKey(initialize, Keyboard.PreviewKeyDownEvent);
        PressKey(initialize, Keyboard.PreviewKeyUpEvent);
        await Wait(() => !ModePressButton.IsHeld(initialize) && !mode.IsInitializationHeld && ReadBool(data, "Action[7]", false) &&
            nodes.ModeWrites().Skip(writesBefore).SequenceEqual([(7, true), (7, false)]),
            "initialization release writes b_Action[7]=False once and reads it back");
        PressKey(initialize, Keyboard.PreviewKeyUpEvent);
        nodes.SetMode(0, 0, initialized: true);
        await Wait(() => mode.ConfirmedState == (0, 0) && mode.Display.Signal(0) == true && ReadBool(data, "Signal[0]", true),
            "PLC returns to stop and supplies the initialization complete feedback");
        Check(nodes.ModeWrites().Length == writesBefore + 2, "repeat key events produced no extra initialization writes");
    }

    private static async Task VerifyPersonnelLocks(Window window, IContainerProvider container, ISession session,
        ModeControlViewModel mode, string secret, FixtureNodes nodes)
    {
        int writesBefore = nodes.ModeWrites().Length;
        var accounts = container.Resolve<IUserAccountRepository>();
        const string first = UserName + "-lock-a", second = UserName + "-lock-b";
        string firstSecret = secret + "-a", secondSecret = secret + "-b";
        SeedAccount(accounts, first, UserRole.Operator, firstSecret);
        SeedAccount(accounts, second, UserRole.EquipmentEngineer, secondSecret);
        var access = container.Resolve<IUserAccessService>();
        var data = container.Resolve<IPlcDataProvider>();
        var locks = await OpenPersonnelLocks();
        Check(!MaintenanceInterlock.Blocked && mode.MaintenancePeople.Count == 0, "the isolated install starts without personnel locks");

        await SubmitLockDialog(FindButton(locks, "人员上锁"), "人员上锁", first, firstSecret, "验证并上锁");
        await Wait(() => mode.MaintenancePeople is [{ UserName: first, Releasing: false }] &&
            MaintenanceInterlock.Blocked && ReadBool(data, "Action[8]", true) &&
            nodes.ModeWrites().Skip(writesBefore).SequenceEqual([(8, true)]),
            "the first verified person locks the equipment and sends one b_Action[8]=True");
        Check(mode.ConfirmedState == (0, 0) && SavedPersonnelLocks() is { PlcHeld: true, People: [{ UserName: first, Releasing: false }] },
            "the first lock is on disk and applies before PLC maintenance mode feedback");
        var diagram = CurrentModeDiagram();
        Check(FindButton(diagram, "人员锁管理").IsEnabled && !FindButton(diagram, "调试开关").IsEnabled &&
            !FindButton(diagram, "按住初始化").IsEnabled && !access.CanControlModes && !access.CanEditParameters,
            "a personnel lock disables equipment operations while personnel management remains available");
        Check(access.CanOpenPage(ViewNames.IoMonitorView) && !access.CanOpenPage(ViewNames.ProcessParameterView),
            "maintenance retains IO monitoring and blocks process parameter operations");
        await VerifyMaintenanceWriteRestrictions(container, nodes, writesBefore + 1);

        var axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
        await Wait(() => axis.ActualPosition is { HasCurrentValue: true } && !axis.CanUseControls &&
            !FindButton(ParameterWorkspace(), "使能").IsEnabled, "axis values remain visible while axis controls are locked");
        var cylinder = await Navigate<CylinderParameterViewModel>(window, "气缸参数", ViewNames.CylinderParameterView);
        await Wait(() => cylinder.Retract?.ArrivalDelay is { HasCurrentValue: true, Edit.CanEdit: false } && !cylinder.CanUseControls &&
            !FindButton(ParameterWorkspace(), "伸出").IsEnabled, "cylinder values remain visible while cylinder controls are locked");
        await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);

        nodes.SetMode(10, 60);
        await Wait(() => mode.ConfirmedState == (10, 60) && MaintenanceInterlock.Blocked, "PLC feedback confirms maintenance mode");
        locks = await OpenPersonnelLocks();
        await SubmitLockDialog(FindButton(locks, "人员上锁"), "人员上锁", second, secondSecret, "验证并上锁");
        await Wait(() => mode.MaintenancePeople is [{ UserName: first }, { UserName: second }] &&
            nodes.ModeWrites().Count(write => write.Bit == 8) == 1, "the second person adds a local lock without resending the PLC request");
        Check(session.CurrentUser.UserName == UserName, "personnel credential checks leave the main HMI identity unchanged");

        var own = await OwnUnlockButton(locks, first);
        await SubmitLockDialog(own, "本人解锁", first, secondSecret, "验证并解锁", expectedError: "用户名或密码错误");
        try
        {
            await mode.MaintainAsync(second, secondSecret, ((MaintenanceInterlock.Person)own.Tag).Id);
            throw new InvalidOperationException("Another person's lock was released.");
        }
        catch (UnauthorizedAccessException error)
        {
            Check(error.Message.Contains("上锁者本人", StringComparison.Ordinal), "valid credentials cannot release another person's lock");
        }
        await SubmitLockDialog(FindButton(locks, "管理员解除全部锁"), "管理员解除全部维护锁", second, secondSecret,
            "管理员确认全部解锁", expectedError: "必须验证管理员账户");
        Check(MaintenanceInterlock.People.Length == 2 && nodes.ModeWrites().Length == writesBefore + 1,
            "invalid credentials and role violations keep both locks and send no PLC write");

        await SubmitLockDialog(await OwnUnlockButton(locks, first), "本人解锁", first, firstSecret, "验证并解锁");
        await Wait(() => mode.MaintenancePeople is [{ UserName: second, Releasing: false }] &&
            MaintenanceInterlock.Blocked && nodes.ModeWrites().Length == writesBefore + 1,
            "the first person can unlock only their own entry without releasing maintenance");
        await SubmitLockDialog(await OwnUnlockButton(locks, second), "本人解锁", second, secondSecret, "验证并解锁");
        await WaitForPendingExit(1, (8, true), (8, false));
        Check(SavedPersonnelLocks() is { PlcHeld: true, People: [{ UserName: second, Releasing: true }] },
            "the final person's pending exit is persisted until PLC feedback");
        await ConfirmMaintenanceExit();

        locks = await OpenPersonnelLocks();
        await SubmitLockDialog(FindButton(locks, "人员上锁"), "人员上锁", first, firstSecret, "验证并上锁");
        await Wait(() => mode.Display.Action(8) == true && ReadBool(data, "Action[8]", true) && MaintenanceInterlock.Blocked,
            "a new lock can be applied after confirmed exit");
        nodes.SetMode(10, 60);
        await Wait(() => mode.ConfirmedState == (10, 60), "PLC confirms the second maintenance entry");
        locks = await OpenPersonnelLocks();
        await SubmitLockDialog(FindButton(locks, "人员上锁"), "人员上锁", second, secondSecret, "验证并上锁");
        await Wait(() => mode.MaintenancePeople.Count == 2 && nodes.ModeWrites().Length == writesBefore + 3,
            "both personnel locks are active before administrator release");
        await SubmitLockDialog(FindButton(locks, "管理员解除全部锁"), "管理员解除全部维护锁", UserName, secret, "管理员确认全部解锁");
        await WaitForPendingExit(2, (8, true), (8, false), (8, true), (8, false));
        Check(SavedPersonnelLocks() is { PlcHeld: true, People: [{ Releasing: true }, { Releasing: true }] },
            "administrator verification persists both pending locks until the PLC exits maintenance");
        await ConfirmMaintenanceExit();

        async Task WaitForPendingExit(int people, params (int Bit, bool Value)[] expected)
        {
            await Wait(() => ReadBool(data, "Action[8]", false) && mode.Display.Action(8) == false &&
                nodes.ModeWrites().Skip(writesBefore).SequenceEqual(expected) && mode.MaintenancePeople.Count == people &&
                mode.MaintenancePeople.All(person => person.Releasing),
                "verified unlock sends b_Action[8]=False once and marks the releasing locks pending");
            Check(mode.ConfirmedState == (10, 60) && MaintenanceInterlock.Blocked && !access.CanControlModes &&
                !FindButton(CurrentModeDiagram(), "调试开关").IsEnabled && !FindButton(CurrentModeDiagram(), "按住初始化").IsEnabled,
                "PLC acknowledgment of the cleared request alone cannot restore controls");
            await VerifyEquipmentWritesBlocked(container);
            Check(nodes.ModeWrites().Skip(writesBefore).SequenceEqual(expected) && nodes.ParameterWrites().Length == 0 &&
                nodes.WriteTrace().Rejected == 0, "blocked writes during pending maintenance exit never reach the OPC UA server");
        }

        async Task ConfirmMaintenanceExit()
        {
            nodes.SetMode(0, 0);
            await Wait(() => mode.ConfirmedState == (0, 0) && mode.MaintenancePeople.Count == 0 &&
                !MaintenanceInterlock.Blocked && access.CanControlModes && FindButton(CurrentModeDiagram(), "调试开关").IsEnabled,
                "PLC stop feedback clears the pending personnel lock and restores permitted controls");
            Check(SavedPersonnelLocks() is { PlcHeld: false, People.Length: 0 }, "confirmed maintenance exit clears the persisted lock");
        }
    }

    private static async Task VerifyMaintenanceWriteRestrictions(IContainerProvider container, FixtureNodes nodes, int expectedWrites)
    {
        await VerifyEquipmentWritesBlocked(container);
        var writer = container.Resolve<IPlcWriteService>();
        var premature = await writer.WriteAsync(writer.Capture("PLC01"), [new(ModeKey("Request.Maintenance"), "False")]);
        Check(premature.Results is [{ State: PlcWriteState.NotSent }] && nodes.ModeWrites().Length == expectedWrites &&
            nodes.ParameterWrites().Length == 0 && nodes.WriteTrace().Rejected == 0,
            "the write entry point rejects bypassing personnel management and nothing reaches the server");
    }

    private static async Task VerifyEquipmentWritesBlocked(IContainerProvider container)
    {
        var writer = container.Resolve<IPlcWriteService>();
        var context = writer.Capture("PLC01");
        (string Key, string Value)[] blocked = [
            ("UnitMode[0].Request.Debug", "True"), ("UnitMode[0].Request.Initialize", "True"), ("UnitMode[0].Action[3]", "True"),
            (JogSpeed, "18"), (ArrivalDelay, "180"), ("Axis[0].Request.Enable", "True"), ("CYL[0].Request.Extend", "True"),
        ];
        foreach (var (key, value) in blocked)
        {
            var rejected = await writer.WriteAsync(context, [new(new(PlcProtocol.OpcUa, "PLC01", key), value)]);
            Check(rejected.Results is [{ State: PlcWriteState.NotSent } result] &&
                result.Message.Contains("维护锁定期间", StringComparison.Ordinal), $"maintenance prevents {key} at the write entry point");
        }
    }

    private static PlcVariableKey ModeKey(string suffix) => new(PlcProtocol.OpcUa, "PLC01", "UnitMode[0]." + suffix);

    private static bool ReadBool(IPlcDataProvider data, string suffix, bool expected) =>
        data.GetSnapshots([ModeKey(suffix)]) is [{ IsValid: true, Quality: PlcObservationQuality.Good, EngineeringValue: bool actual }] &&
        actual == expected;

    private static void PressKey(Button button, RoutedEvent keyEvent) => button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(button)!, Environment.TickCount, Key.Space) { RoutedEvent = keyEvent });

    private static ModeControlView CurrentModeDiagram() =>
        Walk<ModeControlView>((FrameworkElement)Active(RegionNames.WorkspaceRegion)!).Single();

    private static MaintenanceInterlock.Saved? SavedPersonnelLocks()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Config", "maintenance-locks.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<MaintenanceInterlock.Saved>(File.ReadAllText(path)) : null;
    }

    private static async Task<FrameworkElement> OpenPersonnelLocks()
    {
        var diagram = CurrentModeDiagram();
        var panel = (FrameworkElement)diagram.FindName("MaintenanceLockPanel");
        if (!panel.IsVisible) Click(FindButton(diagram, "人员锁管理"));
        await Wait(() => panel.IsVisible, "the personnel lock panel opens");
        return panel;
    }

    private static async Task<Button> OwnUnlockButton(FrameworkElement panel, string account)
    {
        Button? action = null;
        await Wait(() => (action = Walk<Button>(panel).SingleOrDefault(button => button.IsVisible &&
                Equals(button.Content, "本人解锁") && button.Tag is MaintenanceInterlock.Person person && person.UserName == account)) is not null,
            "the selected lock has its own unlock button");
        return action!;
    }

    private static async Task SubmitLockDialog(Button launch, string title, string user, string secret,
        string submit, string? expectedError = null)
    {
        Click(launch);
        Window? dialog = null;
        await Wait(() => (dialog = Application.Current.Windows.OfType<Window>()
            .SingleOrDefault(window => window.IsVisible && window.Title == title)) is not null, $"the {title} credential dialog opens");
        var username = Walk<TextBox>(dialog!).Single();
        if (username.IsReadOnly)
            Check(username.Text == user, "the own-unlock dialog fixes the account to the selected person");
        else
            username.Text = user;
        Walk<PasswordBox>(dialog!).Single().Password = secret;
        var confirm = FindButton(dialog!, submit);
        Click(confirm);
        if (expectedError is null)
            await Wait(() => !dialog!.IsVisible, $"the {title} dialog accepts valid credentials");
        else
        {
            await Wait(() => confirm.IsEnabled && Walk<TextBlock>(dialog!)
                .Any(text => text.Text.Contains(expectedError, StringComparison.Ordinal)), $"the {title} dialog rejects unauthorized credentials");
            Click(FindButton(dialog!, "取消"));
            await Wait(() => !dialog!.IsVisible, "the rejected personnel action can be cancelled");
        }
    }
}

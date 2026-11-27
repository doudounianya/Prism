using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Company.Hmi.Presentation;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Shared.Prism;
using Company.PlcHmi.Shell.Views;
using MahApps.Metro.Controls.Dialogs;
using Prism.Ioc;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private sealed record ExitTiming(long ConfirmToWindowCloseMs, long WindowClosedUnixMs);
    private static ExitTiming? exitTiming;

    private static FixtureMode InitiallyInDebug()
    {
        var actions = new bool[9];
        actions[6] = true;
        return new(0, 30, actions);
    }

    private static bool IsLifecycle(string phase) =>
        phase is "lifecycle" or "lifecycle-restart" or "lifecycle-maintenance-restart" or "lifecycle-unreadable-lock";

    private static async Task VerifyLifecycle(MainWindow window, IContainerProvider container, LoopbackOpcUa server, string phase, string secret)
    {
        var runtime = container.Resolve<IPlcRuntime>();
        var data = container.Resolve<IPlcDataProvider>();
        if (phase == "lifecycle-maintenance-restart")
        {
            await VerifyMaintenanceAfterRestart(window, container, data, server.Nodes);
            return;
        }
        if (phase == "lifecycle-unreadable-lock")
        {
            await VerifyUnreadableLock(window, container, data, server.Nodes);
            return;
        }
        if (phase == "lifecycle-restart")
        {
            var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
            await Wait(() => mode.ConfirmedState == (0, 0) && FindButton(CurrentModeDiagram(), "按住初始化").IsEnabled &&
                ReadBool(data, "Action[3]", false) && ReadBool(data, "Action[6]", false) && ReadBool(data, "Action[7]", false),
                "restarting the same installation restores login, fresh mode feedback and ready controls");
            Check(server.Nodes.ModeWrites().Length == 0 && server.Nodes.ParameterWrites().Length == 0,
                "the restarted HMI does not replay an action from the previous process");
            await PlacePersonnelLock(mode, data, server.Nodes, secret);
            await RequestPersonnelUnlock(mode, data, secret);
            return;
        }

        var axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
        await Wait(() => axis.ActualPosition is { HasCurrentValue: true, Value: "12.5" } && axis.CanUseControls,
            "startup observes PLC-side manual debug and authorizes the first parameter page directly");
        Check(server.Nodes.ModeWrites().Length == 0, "startup debug grants access from readback without writing a debug request");

        var viewModel = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        var debug = FindButton(CurrentModeDiagram(), "调试开关");
        await Wait(() => debug.IsEnabled && viewModel.ConfirmedState == (0, 30) && ReadBool(data, "Action[6]", true),
            "the startup debug session can be exited from the packaged mode screen");
        Click(debug);
        await Wait(() => ReadBool(data, "Action[6]", false) && server.Nodes.ModeWrites().SequenceEqual([(6, false)]),
            "explicit exit clears the prior PLC debug request once");
        server.Nodes.SetMode(0, 0);
        await Wait(() => viewModel.ConfirmedState == (0, 0), "the PLC confirms stop before lifecycle checks");

        await VerifyTray(window, runtime, viewModel, server.Nodes);
        await VerifyRapidModeVisibility(viewModel, server.Nodes);
        await VerifyHeldDisconnect(runtime, data, viewModel, server, bit: 7, mode: 0, status: 0);
        await VerifyHeldDisconnect(runtime, data, viewModel, server, bit: 3, mode: 20, status: 50);

        server.Nodes.SetMode(0, 0);
        var initialize = FindButton(CurrentModeDiagram(), "按住初始化");
        await Wait(() => viewModel.ConfirmedState == (0, 0) && initialize.IsEnabled, "initialization is available before close");
        PressKey(initialize, Keyboard.PreviewKeyDownEvent);
        await Wait(() => viewModel.IsInitializationHeld && ReadBool(data, "Action[7]", true),
            "closing is initiated while the initialization request is held");
    }

    private static async Task PlacePersonnelLock(ModeControlViewModel mode, IPlcDataProvider data, FixtureNodes nodes, string secret)
    {
        var panel = await OpenPersonnelLocks();
        await SubmitLockDialog(FindButton(panel, "人员上锁"), "人员上锁", UserName, secret, "验证并上锁");
        await Wait(() => ReadBool(data, "Action[8]", true), "the current process sets the maintenance request before shutdown");
        nodes.SetMode(10, 60);
        await Wait(() => mode.ConfirmedState == (10, 60) &&
            SavedPersonnelLocks() is { PlcHeld: true, People: [{ UserName: UserName, Releasing: false }] },
            "PLC maintenance state and the persisted person are confirmed");
    }

    private static async Task RequestPersonnelUnlock(ModeControlViewModel mode, IPlcDataProvider data, string secret)
    {
        var panel = await OpenPersonnelLocks();
        await SubmitLockDialog(await OwnUnlockButton(panel, UserName), "本人解锁", UserName, secret, "验证并解锁");
        await Wait(() => ReadBool(data, "Action[8]", false) &&
            SavedPersonnelLocks() is { PlcHeld: true, People: [{ UserName: UserName, Releasing: true }] } &&
            mode.ConfirmedState == (10, 60) && MaintenanceInterlock.Blocked,
            "the last unlock is written and stays pending on disk while the PLC remains in maintenance");
    }

    private static async Task VerifyMaintenanceAfterRestart(Window window, IContainerProvider container,
        IPlcDataProvider data, FixtureNodes nodes)
    {
        var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        var access = container.Resolve<IUserAccessService>();
        await Wait(() => mode.ConfirmedState == (10, 60) && ReadBool(data, "Action[8]", false),
            "the freshly started process reads the released request and the retained PLC maintenance state");
        Check(mode.MaintenancePeople is [{ UserName: UserName, Releasing: true }] && MaintenanceInterlock.Blocked &&
            !access.CanControlModes && !access.CanEditParameters && nodes.ModeWrites().Length == 0,
            "the new process restores the pending named lock without replaying its unlock request");
        await VerifyEquipmentWritesBlocked(container);

        nodes.SetMode(0, 0);
        await Wait(() => mode.ConfirmedState == (0, 0) && mode.MaintenancePeople.Count == 0 &&
            !MaintenanceInterlock.Blocked && access.CanControlModes &&
            SavedPersonnelLocks() is { PlcHeld: false, People.Length: 0 },
            "fresh PLC stop feedback clears the restored pending lock in the view and the file");
    }

    private static async Task VerifyUnreadableLock(Window window, IContainerProvider container, IPlcDataProvider data, FixtureNodes nodes)
    {
        var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        await Wait(() => mode.ConfirmedState == (0, 30) && ReadBool(data, "Action[6]", true),
            "the PLC supplies valid manual debug feedback while the lock record is unreadable");
        var access = container.Resolve<IUserAccessService>();
        Check(MaintenanceInterlock.Blocked && !access.CanControlModes && !access.CanEditParameters,
            "an unreadable lock record preserves the operation block on startup");
        await VerifyMaintenanceWriteRestrictions(container, nodes, 0);
        var axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
        await Wait(() => axis.ActualPosition is { HasCurrentValue: true } && !axis.CanUseControls &&
            !FindButton(ParameterWorkspace(), "使能").IsEnabled, "axis data remains visible and read-only with an unreadable lock record");
        var cylinder = await Navigate<CylinderParameterViewModel>(window, "气缸参数", ViewNames.CylinderParameterView);
        await Wait(() => cylinder.Retract?.ArrivalDelay is { HasCurrentValue: true, Edit.CanEdit: false } &&
            !cylinder.CanUseControls, "cylinder data remains visible and read-only with an unreadable lock record");
    }

    private static async Task VerifyTray(MainWindow window, IPlcRuntime runtime, ModeControlViewModel mode, FixtureNodes nodes)
    {
        var writesBefore = nodes.ModeWrites();
        var cancel = await RequestClose(window);
        Click(FindButton(cancel, "取消"));
        await Wait(() => !cancel.IsVisible && window.IsVisible && runtime.Status.State == PlcRuntimeState.Running,
            "cancelling the prompt keeps the window and runtime active");

        window.WindowState = WindowState.Maximized;
        var epoch = ConnectedEpoch(runtime);
        Check(epoch > 0, "an established PLC session is available before minimizing to tray");
        var prompt = await RequestClose(window);
        Click(FindButton(prompt, "最小化到托盘"));
        await Wait(() => !window.IsVisible && runtime.Status.State == PlcRuntimeState.Running,
            "minimize to tray hides the main window while leaving the runtime running");
        Check(ConnectedEpoch(runtime) == epoch, "hiding to tray preserves the original PLC session");

        // Invoke the menu item owned by the real NotifyIcon so the tray callback runs on this window's dispatcher.
        var tray = (Forms.NotifyIcon?)typeof(MainWindow).GetField("trayIcon", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        Check(tray is { Visible: true, ContextMenuStrip: not null }, "the packaged shell creates its visible tray icon and menu");
        nodes.SetMode(20, 0);
        tray!.ContextMenuStrip!.Items.Cast<Forms.ToolStripItem>().Single(item => item.Text == "显示主窗口").PerformClick();
        await Wait(() => window.IsVisible && window.WindowState == WindowState.Maximized && !tray.Visible &&
            mode.ConfirmedState == (20, 0), "the tray menu restores the maximized window and updates live mode feedback");
        Check(ConnectedEpoch(runtime) == epoch && nodes.ModeWrites().SequenceEqual(writesBefore),
            "cancel, tray hiding and restore send no equipment commands and keep the connection");
    }

    private static async Task VerifyRapidModeVisibility(ModeControlViewModel mode, FixtureNodes nodes)
    {
        var writesBefore = nodes.ModeWrites();
        var operationGate = (SemaphoreSlim?)typeof(ModeControlViewModel)
            .GetField("_operationGate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mode)
            ?? throw new InvalidOperationException("The mode operation gate is missing from the lifecycle fixture.");
        await operationGate.WaitAsync();
        Task hiding, showing;
        try
        {
            // Delay the original visibility callback at the same gate used to clear held buttons.
            hiding = mode.SetVisibleAsync(false);
            showing = mode.SetVisibleAsync(true);
            Check(!hiding.IsCompleted, "the hide callback is still waiting for the mode operation gate");
        }
        finally { operationGate.Release(); }
        await Task.WhenAll(hiding, showing);

        nodes.SetMode(0, 0);
        await Wait(() => mode.ConfirmedState == (0, 0) && mode.Message == "模式控制反馈已更新。",
            "the latest show request keeps mode acquisition active after the delayed hide completes");
        Check(nodes.ModeWrites().SequenceEqual(writesBefore), "rapid visibility changes send no extra PLC commands");
    }

    private static async Task VerifyHeldDisconnect(IPlcRuntime runtime, IPlcDataProvider data, ModeControlViewModel model,
        LoopbackOpcUa server, int bit, short mode, short status)
    {
        string action = $"Action[{bit}]";
        string label = bit == 7 ? "initialization" : "stop";
        bool Held() => bit == 7 ? model.IsInitializationHeld : model.IsStopHeld;
        bool ReleaseUnconfirmed() => model.OperationMessage.StartsWith(
            bit == 7 ? "初始化松开复位未确认" : "停止松开复位未确认", StringComparison.Ordinal);
        Button CurrentButton() => bit == 7 ? FindButton(CurrentModeDiagram(), "按住初始化") :
            Walk<Button>(CurrentModeDiagram()).Single(button => button.IsVisible && Equals(button.Tag, "Stop"));
        var original = server.Nodes;
        original.SetMode(mode, status);
        await Wait(() => model.ConfirmedState == (mode, status) && CurrentButton().IsEnabled && ReadBool(data, action, false),
            $"the {label} button is ready on the original session");
        var button = CurrentButton();
        var writesBefore = original.ModeWrites().Length;
        PressKey(button, Keyboard.PreviewKeyDownEvent);
        await Wait(() => Held() && ReadBool(data, action, true) &&
            original.ModeWrites().Skip(writesBefore).SequenceEqual([(bit, true)]), $"{label} is held and the original server has one set request");

        var epoch = ConnectedEpoch(runtime) ?? throw new InvalidOperationException($"The {label} session was lost before the fault injection.");
        var retainedState = new FixtureMode(mode, status, original.ActionBank());
        server.Disconnect();
        try
        {
            await Wait(() => ConnectedEpoch(runtime) is null && !Held() && !ModePressButton.IsHeld(button) && ReleaseUnconfirmed(),
                $"losing the connection while {label} is held clears the local gesture and reports unconfirmed release");
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"Connection epoch={ConnectedEpoch(runtime)}, session held={Held()}, button held={ModePressButton.IsHeld(button)}, message={model.OperationMessage}", error);
        }
        Check(original.ModeWrites().Skip(writesBefore).SequenceEqual([(bit, true)]),
            $"the disconnected session had no accepted {label} release");

        await server.ConnectAsync(reconnected: true, initialMode: retainedState);
        var restored = server.Nodes;
        try
        {
            await Wait(() => ConnectedEpoch(runtime) > epoch && model.ConfirmedState == (mode, status) &&
                ReadBool(data, action, true) && !CurrentButton().IsEnabled,
                $"the new PLC session still reports the retained {label} bit and keeps the button disabled");
        }
        catch (InvalidOperationException error)
        {
            var snapshot = data.GetSnapshots([ModeKey(action)]).SingleOrDefault();
            throw new InvalidOperationException(
                $"Old epoch={epoch}, new epoch={ConnectedEpoch(runtime)}, state={model.ConfirmedState}, read={snapshot?.State}/{snapshot?.Quality}/{snapshot?.EngineeringValue}, " +
                $"display={model.Display.Action(bit)}, button enabled={button.IsEnabled}, button held={ModePressButton.IsHeld(button)}, " +
                $"server bit={restored.ActionBank()[bit]}, server writes={string.Join(", ", restored.ModeWrites())}, feedback={model.Message}", error);
        }
        PressKey(CurrentButton(), Keyboard.PreviewKeyUpEvent);
        Check(restored.ModeWrites().Length == 0 && restored.ActionBank()[bit], $"reconnect and old key release do not replay {label} to the new session");

        restored.ClearActionAsPlc(bit);
        await Wait(() => ReadBool(data, action, false) && CurrentButton().IsEnabled, $"the PLC-side {label} reset enables a fresh gesture");
        button = CurrentButton();
        PressKey(button, Keyboard.PreviewKeyDownEvent);
        await Wait(() => Held() && ReadBool(data, action, true) && restored.ModeWrites().SequenceEqual([(bit, true)]),
            $"a new {label} press is accepted on the current connection");
        PressKey(button, Keyboard.PreviewKeyUpEvent);
        await Wait(() => !Held() && ReadBool(data, action, false) && restored.ModeWrites().SequenceEqual([(bit, true), (bit, false)]),
            $"the new {label} release is accepted once on the current connection");
    }

    private static long? ConnectedEpoch(IPlcRuntime runtime) =>
        runtime.Status.Devices is [{ DeviceId: "PLC01", State: PlcDeviceState.Connected, ConnectionEpoch: var epoch }] ? epoch : null;

    private static void RecordLifecycleClose(long elapsedMs) =>
        exitTiming = new(elapsedMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static void VerifyLifecycleAfterExit(string phase, FixtureNodes nodes)
    {
        if (phase == "lifecycle")
            Check(nodes.ModeWrites().SequenceEqual([(3, true), (3, false), (7, true), (7, false)]) &&
                !nodes.ActionBank()[7], "closing the application cleared the held initialization request once on the original session");
        else if (phase == "lifecycle-restart")
            Check(nodes.ModeWrites().SequenceEqual([(8, true), (8, false)]) && !nodes.ActionBank()[8] &&
                SavedPersonnelLocks() is { PlcHeld: true, People: [{ Releasing: true }] },
                "closing with maintenance pending retains the lock file and sends no extra equipment commands");
        else
            Check(nodes.ModeWrites().Length == 0, "the restarted application closes without any replayed action");
        Check(nodes.WriteTrace().Rejected == 0 && nodes.ParameterWrites().Length == 0, "the lifecycle fixture saw no unrelated writes");
    }

    private static void WriteExitTiming()
    {
        Check(exitTiming is not null, "exit timing is captured from confirmation through actual window close");
        Console.WriteLine("EXIT_TIMING " + JsonSerializer.Serialize(exitTiming));
    }
}

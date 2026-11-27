using System.Windows;
using System.Windows.Controls;
using Company.Hmi.Presentation;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.Prism;
using Company.PlcHmi.Shell.Views;
using Prism.Ioc;

internal static partial class Program
{
    private const string BackedUpSoftwareName = "Prism PLC HMI 备份样本";
    private const string CandidateSoftwareName = "Prism PLC HMI 候选样本";

    private static bool IsDesktopMaintenance(string mode) => mode is "desktop-lock-seed" or "desktop-lock-upgrade" or "desktop-lock-rollback";

    private static FixtureMode RetainedMaintenance(bool requested)
    {
        var actions = new bool[9];
        actions[8] = requested;
        return new(10, 60, actions);
    }

    private static async Task VerifyDesktopMaintenance(Window window, IContainerProvider container, FixtureNodes nodes,
        string mode, string secret)
    {
        var data = container.Resolve<IPlcDataProvider>();
        if (mode == "desktop-lock-rollback")
        {
            await VerifyMaintenanceAfterRestart(window, container, data, nodes);
            return;
        }

        var model = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        if (mode == "desktop-lock-seed")
        {
            await Wait(() => model.ConfirmedState == (0, 0) && ReadBool(data, "Action[8]", false),
                "the original installation starts with current PLC stop feedback and no maintenance request");
            await PlacePersonnelLock(model, data, nodes, secret);
            return;
        }

        var access = container.Resolve<IUserAccessService>();
        await Wait(() => model.ConfirmedState == (10, 60) && ReadBool(data, "Action[8]", true),
            "the upgraded installation reads the original active PLC maintenance request");
        var personId = container.Resolve<ISession>().CurrentUser.Id;
        Check(model.MaintenancePeople is [{ UserName: UserName, Releasing: false } person] && person.Id == personId &&
            MaintenanceInterlock.Blocked && !access.CanControlModes && !access.CanEditParameters && nodes.ModeWrites().Length == 0,
            "upgrade preserves the named lock and matches it to the restored account without replay");
        await VerifyMaintenanceWriteRestrictions(container, nodes, 0);
        await RequestPersonnelUnlock(model, data, secret);
    }

    private static void VerifyDesktopMaintenanceAfterExit(string mode, FixtureNodes nodes)
    {
        var expected = mode switch
        {
            "desktop-lock-seed" => new[] { (8, true) },
            "desktop-lock-upgrade" => new[] { (8, false) },
            _ => [],
        };
        Check(nodes.ModeWrites().SequenceEqual(expected) && nodes.ParameterWrites().Length == 0 &&
            nodes.WriteTrace().Rejected == 0, "closing the installation leaves only the expected maintenance writes for this phase");
    }

    private static async Task VerifyDesktopRollback(MainWindow window, IContainerProvider container, ISession session,
        LoopbackOpcUa server, string installation)
    {
        var runtime = container.Resolve<IPlcRuntime>();
        var access = container.Resolve<IUserAccessService>();
        var data = container.Resolve<IPlcDataProvider>();
        var initial = server.Nodes;

        var axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
        await Wait(() => axis.ActualPosition is { HasCurrentValue: true, Value: "12.5" } && axis.CanUseControls &&
            FindButton(ParameterWorkspace(), "使能").IsEnabled, "the axis page uses startup PLC debug feedback and reads position 12.5");
        Check(initial.ModeWrites().Length == 0, "startup debug does not send a mode request");

        var cylinder = await Navigate<CylinderParameterViewModel>(window, "气缸参数", ViewNames.CylinderParameterView);
        var page = Walk<CylinderParameterView>(ParameterWorkspace()).Single();
        await Wait(() => cylinder.CanUseControls && cylinder.Retract?.ArrivalDelay is { HasCurrentValue: true, Edit.CanEdit: true },
            "the cylinder page reads the PLC parameter and allows manual debug editing");
        var delay = cylinder.Retract!.ArrivalDelay!;
        await EditThroughDialog(page, delay, "148");
        await Wait(() => delay is { HasCurrentValue: true, Value: "148" } &&
            initial.ParameterWrites().SequenceEqual([(ArrivalDelay, (object)(short)148)]),
            "the cylinder parameter editor writes once and reads back the revised value");
        initial.SetMode(0, 0, debugRequest: false);
        await Wait(() => !access.CanEditParameters && !cylinder.CanUseControls &&
            delay is { HasCurrentValue: true, Edit.CanEdit: false } && ReadBool(data, "Action[6]", false),
            "the cylinder page retains live values and becomes read-only after leaving debug");
        await Navigate<AxisParameterViewModel>(window, "轴参数", ViewNames.AxisParameterView);

        await VerifyOpcUa(window, runtime, server, extraIoGroup: true);
        await VerifyHistoryQueries(window, nightCount: installation == "desktop-live" ? 23 : 17);
        await Navigate<object>(window, "工艺参数", ViewNames.ProcessParameterView);
        await Navigate<object>(window, "操作日志", ViewNames.OperationLogView);
        Check(!FindButton(window, "MES接口").IsEnabled, "the MES entry remains visible and disabled");
        await VerifySystemSettings(window, installation);

        var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        await VerifyTray(window, runtime, mode, server.Nodes);
        await Logout(window, session);
        axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
        await Wait(() => axis.ActualPosition is { HasCurrentValue: true, Value: "12.5" } && !axis.CanUseControls &&
            !FindButton(ParameterWorkspace(), "使能").IsEnabled, "the signed-out user can still read live axis values");
        Check(initial.ModeWrites().SequenceEqual([(3, true), (3, false)]) &&
            initial.ParameterWrites().SequenceEqual([(ArrivalDelay, (object)(short)148)]) &&
            server.Nodes.ModeWrites().Length == 0 && server.Nodes.ParameterWrites().Length == 0 &&
            initial.WriteTrace().Rejected == 0 && server.Nodes.WriteTrace().Rejected == 0,
            "both connections recorded only the expected stop handshake and cylinder edit");
    }

    private static async Task VerifySystemSettings(Window owner, string installation)
    {
        Click(FindButton(owner, "系统设置"));
        Window? settings = null;
        await Wait(() => (settings = Application.Current.Windows.OfType<Window>()
            .SingleOrDefault(window => window != owner && window.IsVisible && window.Title == "系统设置")) is not null &&
            Walk<TextBlock>(settings).Any(text => text.IsVisible && text.Text is "软件名称" or "本人密码"),
            "system settings open with the PLC HMI settings content");
        var softwareName = Walk<TextBox>(settings!).Single(control =>
            control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SystemConfigManager.Config.SoftwareConfig.Name");
        if (installation is "desktop-live" or "desktop-restore")
            Check(softwareName.Text == BackedUpSoftwareName, "system settings read the backed-up software name after restart");

        if (installation == "desktop-restore")
        {
            Click(FindButton(settings!, "取消"));
            await Wait(() => !settings!.IsVisible, "cancelling settings returns to the workspace");
            return;
        }
        var name = installation == "desktop-seed" ? BackedUpSoftwareName : CandidateSoftwareName;
        softwareName.SetCurrentValue(TextBox.TextProperty, name);
        softwareName.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Click(FindButton(settings!, "确定"));
        await Wait(() => !settings!.IsVisible, "administrator saves software settings through the packaged dialog");
    }
}

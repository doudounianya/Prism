using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Company.Database.Accounts;
using Company.Hmi.Contracts;
using Company.Hmi.Presentation;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Shared.Prism;
using Prism.Ioc;

internal static partial class Program
{
    private const string JogSpeed = "Axis[0].st_Parameter.i_Jog_Vel";
    private const string EnableFeedback = "Axis[0].st_Action.b_Power_Execute";
    private const string RunFeedback = "Axis[0].st_Action.b_Abs_On[0]";
    private const string ArrivalDelay = "CYL[0].Parameters.Retract.ArrivalDelay";
    private const string RetractFeedback = "CYL[0].Retract.CommandFeedback";
    private const string ExtendFeedback = "CYL[0].Extend.CommandFeedback";

    private static void ValidateParameterBindings(PlcVariableBinding[] points, Dictionary<string, PlcVariableBinding> writable)
    {
        int aliases = ParameterPoint.All.Count(point => point.Command is not null);
        Check(points.Length == 50 + ParameterPoint.All.Count + aliases &&
            writable.Count == 2 + aliases + ParameterPoint.All.Count(point => point.WriteMode is not null) &&
            writable.GetValueOrDefault("UnitMode[0].Request.Debug") is
                { WriteMode: "保持请求", Address: OpcUaBindingAddress { NodeIdentifier: "s=" + FixtureNodes.StopBankNode, ArrayIndex: 6 } },
            "the parameter fixture adds only the declared axis, cylinder and debug bindings");

        var all = points.ToDictionary(point => point.LogicalId);
        bool Matches(ParameterPoint spec, string key, PlcAccess access, string writeMode) =>
            all.GetValueOrDefault(key) is { Address: OpcUaBindingAddress { ArrayIndex: null } address } binding &&
            address.NodeIdentifier == "s=" + spec.Node && binding.Access == access &&
            binding.WriteMode == writeMode && binding.DataType.ToString() == spec.Type &&
            binding.Category == (key.StartsWith("Axis[", StringComparison.Ordinal) ? "轴" : "气缸");
        Check(ParameterPoint.All.All(spec =>
            Matches(spec, spec.Key, spec.WriteOnly ? PlcAccess.Write : spec.WriteMode is null ? PlcAccess.Read : PlcAccess.ReadWrite,
                spec.WriteMode ?? "只读") && (spec.Command is null || Matches(spec, spec.Command, PlcAccess.Write, "握手请求"))),
            "parameter and action aliases resolve exactly to the fixture's typed OPC UA nodes");
    }

    private static async Task VerifyParameterPermissions(Window window, IContainerProvider container, ISession session,
        IUserAccountRepository accounts, string secret, FixtureNodes nodes)
    {
        var access = container.Resolve<IUserAccessService>();
        Check(access.CanOpenPage(ViewNames.AxisParameterView) && access.CanOpenPage(ViewNames.CylinderParameterView) &&
            !access.CanEditParameters, "the page policy allows anonymous read access and denies anonymous edits");

        UserRole?[] roles = [UserRole.Operator, null, UserRole.EquipmentEngineer, UserRole.ProcessEngineer, UserRole.Administrator];
        foreach (var role in roles.OfType<UserRole>())
            SeedAccount(accounts, UserName + "-" + (int)role, role, secret);

        IPlcDataProvider? provider = null;
        PlcVariableKey[] feedbackKeys = new[] { "Mode", "Status", "Action[6]" }
            .Select(name => new PlcVariableKey(PlcProtocol.OpcUa, "PLC01", "UnitMode[0]." + name)).ToArray();
        async Task ObserveMode(short mode, short status, bool requested)
        {
            nodes.SetMode(mode, status, requested);
            await Wait(() =>
            {
                var feedback = provider!.GetSnapshots(feedbackKeys).ToDictionary(snapshot => snapshot.Key);
                return feedbackKeys.All(key => feedback.TryGetValue(key, out var item) && item.IsValid && item.EngineeringValue is not null) &&
                    Convert.ToInt32(feedback[feedbackKeys[0]].EngineeringValue) == mode &&
                    Convert.ToInt32(feedback[feedbackKeys[1]].EngineeringValue) == status &&
                    Equals(feedback[feedbackKeys[2]].EngineeringValue, requested);
            }, $"active page observes OPC UA mode {mode}, status {status}, debug request {requested}");
        }

        int expectedWrites = 0;
        foreach (var role in roles)
        {
            bool mayEdit = role is not (null or UserRole.Operator);
            string actor = role?.ToString() ?? "Anonymous";
            int roleNumber = (int)role.GetValueOrDefault();
            nodes.SetMode(0, mayEdit ? (short)0 : (short)30, !mayEdit);
            if (role is { } userRole)
            {
                string userName = UserName + "-" + (int)userRole;
                if (userRole == UserRole.Operator) await Login(session, userName, userRole, secret);
                else await SignInFromWorkspace(window, session, userName, userRole, secret);
            }
            else
                Check(session.CurrentUser.Id == 0, "the anonymous workspace has no authenticated identity");
            provider ??= container.Resolve<IPlcDataProvider>();

            var axis = await Navigate<AxisParameterViewModel>(window, "参数设置", ViewNames.AxisParameterView);
            var axisPage = Walk<Company.Hmi.Presentation.AxisParameterView>(ParameterWorkspace()).Single();
            ((TabControl)axisPage.FindName("AxisPositionTabs")).SelectedIndex = 0;
            await Wait(() => axis.ActualPosition is { HasCurrentValue: true, Value: "12.5" },
                $"{actor} can view live axis position");
            await Wait(() => axis.ManualParameters.Any(parameter => parameter.Field is { LogicalId: JogSpeed, HasCurrentValue: true }) &&
                axis.AbsolutePositions.Any(row => row is { Index: 0, Position.HasCurrentValue: true, Position.Value: "20" }),
                $"{actor} can view jog speed and the configured absolute position");
            var speed = axis.ManualParameters.Single(parameter => parameter.Field?.LogicalId == JogSpeed).Field!;
            var position = axis.AbsolutePositions.Single(row => row.Index == 0);
            ((DataGrid)axisPage.FindName("AbsolutePositionTable")).ScrollIntoView(position);
            Button? runButton = null;
            await Wait(() => (runButton = Walk<Button>(axisPage).SingleOrDefault(button =>
                button.IsVisible && ReferenceEquals(button.DataContext, position))) is not null, "the configured axis position has a visible run button");
            await VerifyPage(axis, axisPage, speed, "Enable", 6, "axis", async button =>
            {
                string value = (8 + roleNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await EditThroughDialog(axisPage, speed, value);
                await Wait(() => speed.HasCurrentValue && speed.Value == value && axis.CanOperate &&
                    axis.OperationMessage == "参数已写入并回读一致。", $"{role} writes axis jog speed and reads it back");
                bool expected = axis.EnableState!.BooleanValue == false;
                Click(button);
                await Wait(() => axis.EnableState.BooleanValue == expected && axis.CanOperate &&
                    nodes.ParameterWrites().Length >= expectedWrites + 2, $"{role} operates the axis enable button");
                bool run = position.RunState!.BooleanValue == false;
                Click(runButton!);
                await Wait(() => position.RunState.BooleanValue == run && axis.CanOperate &&
                    nodes.ParameterWrites().Length >= expectedWrites + 3, $"{role} operates the configured absolute position run button");
                Check(nodes.ParameterWrites().Skip(expectedWrites).SequenceEqual(
                    [(JogSpeed, (object)double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), (EnableFeedback, expected),
                        (RunFeedback, run)]), $"{role} sends exactly the axis parameter, enable and position run writes");
                expectedWrites += 3;
            }, runButton);

            var cylinder = await Navigate<CylinderParameterViewModel>(window, "气缸参数", ViewNames.CylinderParameterView);
            var cylinderPage = Walk<Company.Hmi.Presentation.CylinderParameterView>(ParameterWorkspace()).Single();
            await Wait(() => cylinder.Retract is { Elapsed.HasCurrentValue: true, Elapsed.Value: "34", ArrivalDelay.HasCurrentValue: true,
                CommandFeedback.BooleanValue: not null } && cylinder.Extend?.CommandFeedback?.BooleanValue is not null,
                $"{actor} can view live cylinder timing and parameters");
            var delay = cylinder.Retract!.ArrivalDelay!;
            bool extending = cylinder.Extend!.CommandFeedback!.BooleanValue != true;
            await VerifyPage(cylinder, cylinderPage, delay, extending ? "Extend" : "Retract", 3, "cylinder", async button =>
            {
                short value = (short)(120 + roleNumber);
                var input = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await EditThroughDialog(cylinderPage, delay, input);
                await Wait(() => delay.HasCurrentValue && delay.Value == input && cylinder.CanOperate &&
                    cylinder.OperationMessage == "参数已写入并回读一致。", $"{role} writes cylinder arrival delay and reads it back");
                Click(button);
                await Wait(() => cylinder.Extend.CommandFeedback!.BooleanValue == extending &&
                    cylinder.Retract.CommandFeedback!.BooleanValue == !extending && cylinder.CanOperate &&
                    nodes.ParameterWrites().Length >= expectedWrites + 3, $"{role} operates a cylinder direction button");
                Check(nodes.ParameterWrites().Skip(expectedWrites).SequenceEqual(
                    [(ArrivalDelay, (object)value), (extending ? RetractFeedback : ExtendFeedback, false),
                        (extending ? ExtendFeedback : RetractFeedback, true)]),
                    $"{role} sends the cylinder parameter and clears the opposite direction before setting the requested direction");
                expectedWrites += 3;
            });

            if (role is not null) await Logout(window, session);

            async Task VerifyPage(ComponentParameterViewModel model, FrameworkElement page, PlcDisplayField field,
                string action, int buttonCount, string name, Func<Button, Task> exercise, Button? positionRun = null)
            {
                var buttons = Walk<Button>(page).Where(button => button.IsVisible && button.Tag is
                    "Enable" or "Home" or "Stop" or "Positive" or "Negative" or "Reset" or "Retract" or "Extend").ToArray();
                var button = buttons.Single(item => Equals(item.Tag, action));
                Check(buttons.Length == buttonCount && FindParameterBox(page, field).IsReadOnly,
                    $"{actor} sees the {name} operation controls and parameter display");

                await ObserveMode(0, mayEdit ? (short)0 : (short)30, !mayEdit);
                await ReadOnly(mayEdit ? "manual stopped" : "manual debug without role permission");
                if (!mayEdit) return;

                await ObserveMode(20, 30, true);
                await ReadOnly("debug-state feedback outside manual mode");
                await ObserveMode(0, 30, true);
                var parameterPage = (IParameterPage)model;
                await Wait(() => model.CanOperate && buttons.All(item => item.IsEnabled) && field.Edit.CanEdit && positionRun?.IsEnabled != false,
                    $"{role} can edit and use the {name} controls after confirmed manual debug");
                await exercise(button);

                var previousEntry = parameterPage.BeginEntry(field);
                Check(previousEntry is not null, $"{role} can open the {name} editor before leaving debug");
                await ObserveMode(0, 0, false);
                await ReadOnly("after exiting debug with the page still open");
                await previousEntry!.CommitAsync("500");
                Check(nodes.ParameterWrites().Length == expectedWrites, $"a previously opened {name} editor cannot submit after exit");

                async Task ReadOnly(string reason)
                {
                    await Wait(() => !access.CanEditParameters && !model.CanUseControls && !field.Edit.CanEdit &&
                        field.HasCurrentValue && buttons.All(item => !item.IsEnabled) && positionRun?.IsEnabled != true,
                    $"{actor} keeps {name} values visible and controls read-only {reason}");
                    Check(((IParameterPage)model).BeginEntry(field) is null, $"the {name} editor cannot open {reason}");
                    // Route the click to the actual handler to verify that the command also rejects operations.
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await ((IParameterPage)model).OperateAsync(action);
                    Check(nodes.ParameterWrites().Length == expectedWrites, $"the {name} action sends no PLC write {reason}");
                }
            }
        }

        Check(nodes.ParameterWrites().Length == expectedWrites && nodes.WriteTrace() is { Stop.Length: 0, Rejected: 0 },
            "the fixture observed only the expected authorized parameter and component writes");
    }

    private static FrameworkElement ParameterWorkspace() => (FrameworkElement)Active(RegionNames.WorkspaceRegion)!;

    private static async Task SignInFromWorkspace(Window window, ISession session, string userName, UserRole role, string secret)
    {
        var account = Walk<Button>(window).Single(button => button.IsVisible && Equals(button.ToolTip, "登录账号"));
        Click(account);
        UserControl? dialog = null;
        await Wait(() => (dialog = Application.Current.Windows.OfType<Window>().Where(candidate => candidate.IsVisible && candidate != window)
            .SelectMany(Walk<UserControl>).SingleOrDefault(view => view.GetType().Name == "SignInView")) is not null,
            "the workspace sign-in dialog opens");
        try
        {
            var user = Walk<TextBox>(dialog!).Single();
            user.SetCurrentValue(TextBox.TextProperty, userName);
            user.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Walk<PasswordBox>(dialog!).Single().Password = secret;
            Click(FindButton(dialog!, "登录"));
            await Wait(() => session.CurrentUser is { Id: > 0 } current && current.UserName == userName &&
                current.Role == (int)role && !dialog!.IsVisible, $"workspace login authenticates {role}");
        }
        finally
        {
            if (dialog?.IsVisible == true) Window.GetWindow(dialog)?.Close();
        }
    }

    private static TextBox FindParameterBox(FrameworkElement page, PlcDisplayField field) =>
        Walk<TextBox>(page).Single(box => box.IsVisible && ReferenceEquals(box.DataContext, field));

    private static async Task EditThroughDialog(FrameworkElement page, PlcDisplayField field, string value)
    {
        var box = FindParameterBox(page, field);
        var opening = box.Dispatcher.InvokeAsync(() => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(box)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
        ParameterEntryDialog? dialog = null;
        try
        {
            await Wait(() => (dialog = Application.Current.Windows.OfType<ParameterEntryDialog>()
                .SingleOrDefault(window => window.IsVisible)) is not null, "the packaged numeric parameter editor opens");
            ((TextBox)dialog!.FindName("Input")).Text = value;
            Click((Button)dialog.FindName("ConfirmButton"));
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close();
        }
    }
}

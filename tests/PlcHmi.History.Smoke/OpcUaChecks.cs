using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Company.Hmi.Presentation;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Shared.Prism;

internal static partial class Program
{
    private static async Task ValidateLoopbackWorkbook(IPlcBindingConfigProvider provider, LoopbackOpcUa opcua)
    {
        var loaded = await provider.LoadAsync();
        if (!loaded.Success)
            Console.Error.WriteLine(string.Join(Environment.NewLine, loaded.Diagnostics.Where(item => item.Severity == PlcDiagnosticSeverity.Error)));
        Check(loaded.Success, "loopback workbook accepted before runtime starts");
        var configuration = loaded.Configuration!;
        var devices = configuration.Devices.Where(device => device.Enabled).ToArray();
        Check(devices is [{ Id: "PLC01", Protocol: PlcProtocol.OpcUa, SecurityMode: "None", CredentialReference: "" }] &&
            devices[0].OpcUaEndpoint == opcua.Endpoint, "exactly one configured device targets the loopback endpoint");
        var points = configuration.ActiveVariables.ToArray();
        const string stopAddress = "s=" + FixtureNodes.StopBankNode;
        Check(points.Length == 49 && points.All(point => point.Address is OpcUaBindingAddress { NamespaceUri: LoopbackOpcUa.AddressSpace }) &&
            points.Where(point => point.Access.HasFlag(PlcAccess.Write)).ToArray() is
                [{ LogicalId: "UnitMode[0].Action[3]", WriteMode: "握手请求", Address: OpcUaBindingAddress { NodeIdentifier: stopAddress, ArrayIndex: 3 } }],
            "only b_Action[3] is writable among the synthetic OPC UA bindings");
    }

    private static async Task VerifyOpcUa(Window window, IPlcRuntime runtime, LoopbackOpcUa server)
    {
        PlcDeviceRuntimeStatus? ConnectedDevice() => runtime.Status.Devices is [{ DeviceId: "PLC01", State: PlcDeviceState.Connected } device]
            ? device : null;
        await Wait(() => ConnectedDevice() is not null, "PLC HMI establishes an OPC UA session to the loopback server");

        var mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        var diagram = Walk<ModeControlView>((FrameworkElement)Active(RegionNames.WorkspaceRegion)!).Single();
        RadioButton Tab(string name) => (RadioButton)diagram.FindName(name + "Tab");

        async Task Confirm(short actualMode, short status, string tab)
        {
            server.Nodes.SetMode(actualMode, status);
            await Wait(() => mode.ConfirmedState == ((int)actualMode, (int)status) && mode.Display.Status == status && Tab(tab).IsChecked == true,
                $"OPC UA feedback {actualMode}/{status} selects {tab}");
        }

        foreach (short status in new short[] { 0, 10, 20, 30, 40 }) await Confirm(0, status, "Manual");
        await Confirm(20, 50, "Automatic");
        await Confirm(10, 60, "Maintenance");
        await Confirm(0, 70, "Emergency");
        await Confirm(0, 0, "Manual");
        Tab("Automatic").IsChecked = true;
        await Task.Delay(450);
        Check(mode.Display.Mode == 0 && mode.ConfirmedState == (0, 0) && Tab("Automatic").IsChecked == true,
            "stable feedback preserves the browsed tab while retaining actual manual mode");
        await Confirm(0, 30, "Manual");

        await Navigate<object>(window, "设备监控", ViewNames.DeviceMonitorView);
        server.Nodes.SetMode(20, 50);
        await Task.Delay(450);
        Check(Active(RegionNames.WorkspaceRegion)?.GetType().Name == ViewNames.DeviceMonitorView,
            "feedback does not switch the top-level page");
        mode = await Navigate<ModeControlViewModel>(window, "模式控制", ViewNames.ModeControlView);
        diagram = Walk<ModeControlView>((FrameworkElement)Active(RegionNames.WorkspaceRegion)!).Single();
        await Wait(() => mode.ConfirmedState == (20, 50) && Tab("Automatic").IsChecked == true,
            "returning to mode control displays the actual automatic run state");

        await Wait(() => mode.Display.CanStop, "stop is enabled on confirmed feedback and the fixture write binding");
        var stop = Walk<Button>(diagram).Single(button => button.IsVisible && Equals(button.Tag, "Stop"));
        Check(stop.IsEnabled && ModePressButton.GetUseHold(stop), "the visible stop button uses the hold gesture");
        var nodesBefore = server.Nodes;
        var bankBefore = nodesBefore.ActionBank();
        Check(nodesBefore.WriteTrace() is { Stop.Length: 0, Rejected: 0 } && !bankBefore[3],
            "view navigation and PLC feedback send no commands");

        void StopKey(RoutedEvent route) => stop.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(stop)!, Environment.TickCount, Key.Space) { RoutedEvent = route });

        StopKey(Keyboard.PreviewKeyDownEvent);
        await Wait(() => ModePressButton.IsHeld(stop) && nodesBefore.ActionBank()[3] && mode.Display.Action(3) == true &&
            nodesBefore.WriteTrace().Stop.SequenceEqual([true]), "stop press sends True once and reads back True");
        StopKey(Keyboard.PreviewKeyDownEvent);
        StopKey(Keyboard.PreviewKeyUpEvent);
        await Wait(() => !ModePressButton.IsHeld(stop) && !nodesBefore.ActionBank()[3] && mode.Display.Action(3) == false &&
            nodesBefore.WriteTrace().Stop.SequenceEqual([true, false]), "stop release sends False once and reads back False");
        StopKey(Keyboard.PreviewKeyUpEvent);
        Check(nodesBefore.ActionBank().SequenceEqual(bankBefore) && nodesBefore.WriteTrace() is { Rejected: 0 },
            "the stop handshake preserves all neighboring bits and writes no unrelated node");

        var monitor = await Navigate<IoMonitorViewModel>(window, "IO监控", ViewNames.IoMonitorView);
        var io = (FrameworkElement)Active(RegionNames.WorkspaceRegion)!;
        var grid = Walk<DataGrid>(io).Single();
        await Wait(() => monitor.Items.Count == 16 && monitor.Items[0].LogicalId == "IO.DI[0].00" &&
            monitor.Items[0].Value == true && monitor.Items[1].Value == false, "live DI initially shows ON and OFF from the first session");
        Check(grid.IsReadOnly, "the IO grid is read-only");
        var type = Walk<ComboBox>(io).Single(box => box.Items.Contains("DO"));
        type.SelectedItem = "DO";
        await Wait(() => monitor.Items[0].LogicalId == "IO.DO[0].00" && monitor.Items[0].Value == true,
            "DO selection reads live output feedback without writing");
        type.SelectedItem = "DI";
        await Wait(() => monitor.Items[0].LogicalId == "IO.DI[0].00" && monitor.Items[0].Value == true,
            "returning to DI displays the original session values");
        long previousEpoch = ConnectedDevice()?.ConnectionEpoch ?? throw new InvalidOperationException("The initial PLC session was lost.");

        server.Disconnect();
        await Wait(() => ConnectedDevice() is null && monitor.Items.Count == 16 && monitor.Items.All(item => item.Value is null),
            "OPC UA disconnect changes current IO values to unknown");
        await server.ConnectAsync(reconnected: true);
        await Wait(() => ConnectedDevice() is { ConnectionEpoch: var epoch } && epoch > previousEpoch &&
            monitor.Items.Count == 16 && monitor.Items[0].Value == false && monitor.Items[1].Value == true,
            "reconnect advances the session epoch and displays only the new DI values");
        Check(nodesBefore.WriteTrace() is { Rejected: 0 } old && old.Stop.SequenceEqual([true, false]) &&
            server.Nodes.WriteTrace() is { Stop.Length: 0, Rejected: 0 }, "repeated key events, IO navigation and reconnect add no writes");
    }
}

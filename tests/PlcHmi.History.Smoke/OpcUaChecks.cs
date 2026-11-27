using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Company.Hmi.Presentation;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Shared.Prism;

internal static partial class Program
{
    private static async Task ValidateLoopbackWorkbook(IPlcBindingConfigProvider provider, LoopbackOpcUa opcua,
        bool verifyParameters, bool verifyModeOperations, bool extraIoGroup = false, LoopbackOpcUa? secondIo = null)
    {
        var loaded = await provider.LoadAsync();
        if (!loaded.Success)
            Console.Error.WriteLine(string.Join(Environment.NewLine, loaded.Diagnostics.Where(item => item.Severity == PlcDiagnosticSeverity.Error)));
        Check(loaded.Success, "loopback workbook accepted before runtime starts");
        var configuration = loaded.Configuration!;
        var devices = configuration.Devices.Where(device => device.Enabled).ToArray();
        var endpoints = new Dictionary<string, string> { ["PLC01"] = opcua.Endpoint };
        if (secondIo is not null) endpoints.Add("PLC02", secondIo.Endpoint);
        Check(devices.Length == endpoints.Count && devices.All(device =>
                device is { Protocol: PlcProtocol.OpcUa, SecurityMode: "None", CredentialReference: "" } &&
                endpoints.TryGetValue(device.Id, out var endpoint) && device.OpcUaEndpoint == endpoint),
            "all active devices target only their declared IPv4 loopback endpoints");
        var allPoints = configuration.ActiveVariables.ToArray();
        Check(allPoints.All(point => endpoints.ContainsKey(point.DeviceId) &&
                point.Address is OpcUaBindingAddress { NamespaceUri: LoopbackOpcUa.AddressSpace }),
            "all active bindings use only the synthetic namespace");
        if (secondIo?.SharedIo is { } secondarySample)
        {
            var secondary = allPoints.Where(point => point.DeviceId == "PLC02").ToDictionary(point => point.LogicalId);
            bool MatchesBank(string type, int count) => Enumerable.Range(0, count).All(channel =>
                secondary.GetValueOrDefault($"IO.{type}[0].{channel:00}") is
                { Category: "IO", Access: PlcAccess.Read, DataType: PlcDataType.Bool,
                    Address: OpcUaBindingAddress { NodeIdentifier: var node, ArrayIndex: var index } } &&
                node == "s=" + type && index == channel);
            Check(secondary.Count == secondarySample.DiBefore.Length + secondarySample.DoBefore.Length &&
                MatchesBank("DI", secondarySample.DiBefore.Length) && MatchesBank("DO", secondarySample.DoBefore.Length),
                "PLC02 has only the expected read-only DI and DO points");
        }
        var points = allPoints.Where(point => point.DeviceId == "PLC01").ToArray();
        const string stopAddress = "s=" + FixtureNodes.StopBankNode;
        var writable = allPoints.Where(point => point.Access.HasFlag(PlcAccess.Write)).ToDictionary(point => point.LogicalId);
        Check(writable.GetValueOrDefault("UnitMode[0].Action[3]") is
                { DeviceId: "PLC01", WriteMode: "握手请求", Address: OpcUaBindingAddress { NodeIdentifier: stopAddress, ArrayIndex: 3 } },
            "the stop binding targets PLC01 b_Action[3]");
        if (verifyModeOperations)
        {
            Check(writable.GetValueOrDefault("UnitMode[0].Request.Initialize") is
                    { Access: PlcAccess.Write, DataType: PlcDataType.Bool, WriteMode: "握手请求",
                        Address: OpcUaBindingAddress { NodeIdentifier: stopAddress, ArrayIndex: 7 } } &&
                writable.GetValueOrDefault("UnitMode[0].Request.Maintenance") is
                    { Access: PlcAccess.Write, DataType: PlcDataType.Bool, WriteMode: "保持请求",
                        Address: OpcUaBindingAddress { NodeIdentifier: stopAddress, ArrayIndex: 8 } },
                "initialization and maintenance have only their declared write aliases at b_Action[7] and b_Action[8]");
            writable.Remove("UnitMode[0].Request.Initialize");
            writable.Remove("UnitMode[0].Request.Maintenance");
            points = points.Where(point => point.LogicalId is not ("UnitMode[0].Request.Initialize" or "UnitMode[0].Request.Maintenance")).ToArray();
        }
        if (extraIoGroup)
        {
            const string prefix = "IO.DI[1].";
            var byId = points.ToDictionary(point => point.LogicalId);
            Check(Enumerable.Range(0, 16).All(index => byId.GetValueOrDefault($"{prefix}{index:00}") is
                { Access: PlcAccess.Read, DataType: PlcDataType.Bool, Address: OpcUaBindingAddress
                    { NodeIdentifier: "s=DI1", ArrayIndex: var arrayIndex } } && index == arrayIndex),
                "the restored second DI group binds to 16 read-only loopback channels");
            points = points.Where(point => !point.LogicalId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        }
        if (verifyParameters)
            ValidateParameterBindings(points, writable);
        else
        {
            int ioCount = opcua.SharedIo is { } shared ? shared.DiBefore.Length + shared.DoBefore.Length : 32;
            Check(points.Length == 17 + ioCount && writable.Count == 1,
                "only b_Action[3] is writable among the synthetic OPC UA bindings");
        }
    }

    private static async Task VerifyOpcUa(Window window, IPlcRuntime runtime, LoopbackOpcUa server,
        bool extraIoGroup = false, LoopbackOpcUa? secondIo = null)
    {
        PlcDeviceRuntimeStatus? ConnectedDevice() =>
            runtime.Status.Devices.FirstOrDefault(device => device is { DeviceId: "PLC01", State: PlcDeviceState.Connected });
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
        Check(nodesBefore.ModeWrites().Length == 0 && nodesBefore.WriteTrace().Rejected == 0 && !bankBefore[3],
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
        var sharedIo = server.SharedIo;
        if (sharedIo is not null)
            await Wait(() => SharedIoFixture.Matches(monitor, "DI", sharedIo.DiBefore),
                "shared fixture matches all initial DI rows, quality, timestamps and unconfigured channels");
        if (extraIoGroup)
        {
            Check(Walk<Company.Hmi.Presentation.IoMonitorView>(io).Any(), "the packaged PLC loads the shared IO page");
            grid.SelectedIndex = 1;
            await Wait(() => Walk<TextBlock>(io).Any(text => text.Text == monitor.Items[1].BindingDetails),
                "selecting channel 01 displays its restored binding in the details panel");
            var group = Walk<ComboBox>(io).Single(box => box.Items.Contains("1"));
            group.SelectedItem = "1";
            await Wait(() => monitor.Items.Count == 16 && monitor.Items[0].LogicalId == "IO.DI[1].00" &&
                monitor.Items[0].Value == false && monitor.Items[1].Value == true, "the second DI group displays only its own current values");
            group.SelectedItem = "0";
            await Wait(() => monitor.Items.Count == 16 && monitor.Items[0].LogicalId == "IO.DI[0].00" &&
                monitor.Items[0].Value == true, "switching back restores the first group's live values");
        }
        var type = Walk<ComboBox>(io).Single(box => box.Items.Contains("DO"));
        type.SelectedItem = "DO";
        await Wait(() => monitor.Items[0].LogicalId == "IO.DO[0].00" && monitor.Items[0].Value == true,
            "DO selection reads live output feedback without writing");
        if (sharedIo is not null)
            await Wait(() => SharedIoFixture.Matches(monitor, "DO", sharedIo.DoBefore),
                "shared fixture matches all initial DO rows, quality, timestamps and unconfigured channels");
        type.SelectedItem = "DI";
        await Wait(() => monitor.Items[0].LogicalId == "IO.DI[0].00" && monitor.Items[0].Value == true,
            "returning to DI displays the original session values");
        long previousEpoch = ConnectedDevice()?.ConnectionEpoch ?? throw new InvalidOperationException("The initial PLC session was lost.");

        server.Disconnect();
        await Wait(() => ConnectedDevice() is null && monitor.Items.Count == 16 && monitor.Items.All(item => item.Value is null),
            "OPC UA disconnect changes current IO values to unknown");
        if (sharedIo is not null)
            Check(monitor.Items[0].LastValueText == "True" && monitor.Items[1].LastValueText == "False" &&
                monitor.Items[15].StateText == "未配置 / 禁用", "disconnect preserves historical DI and keeps unconfigured channels separate");
        await server.ConnectAsync(reconnected: true);
        await Wait(() => ConnectedDevice() is { ConnectionEpoch: var epoch } && epoch > previousEpoch &&
            monitor.Items.Count == 16 && monitor.Items[0].Value == false && monitor.Items[1].Value == true,
            "reconnect advances the session epoch and displays only the new DI values");
        if (sharedIo is not null)
        {
            await Wait(() => SharedIoFixture.Matches(monitor, "DI", sharedIo.DiAfter),
                "shared fixture matches all reconnected DI rows");
            type.SelectedItem = "DO";
            await Wait(() => SharedIoFixture.Matches(monitor, "DO", sharedIo.DoAfter),
                "shared fixture matches all reconnected DO rows");
            type.SelectedItem = "DI";
            await Wait(() => SharedIoFixture.Matches(monitor, "DI", sharedIo.DiAfter),
                "switching back to DI reads the reconnected session");
        }
        Check(nodesBefore.ModeWrites().SequenceEqual([(3, true), (3, false)]) && nodesBefore.WriteTrace().Rejected == 0 &&
            server.Nodes.ModeWrites().Length == 0 && server.Nodes.WriteTrace().Rejected == 0,
            "repeated key events, IO navigation and reconnect add no writes");
        if (extraIoGroup)
        {
            var current = ConnectedDevice()!;
            var signal = new IoSignalItem { Channel = "00", LogicalId = monitor.Items[0].LogicalId, DisplayName = "fixture",
                Key = monitor.Items[0].Key };
            var now = DateTimeOffset.UtcNow;
            var stamp = runtime.Status.ConfigurationVersion!;
            signal.BeginRefresh(current.ConnectionEpoch, stamp, now.AddSeconds(-1));
            var sample = new PlcVariableSnapshot { Key = signal.Key!, EngineeringValue = true, State = PlcValueState.Valid,
                Quality = PlcObservationQuality.Good, ConnectionEpoch = previousEpoch, ConfigurationVersion = stamp,
                LastValidAt = now, LastAttemptAt = now };
            signal.Apply(sample);
            Check(signal.Value is null && signal.LastValueText == "—", "the IO page model rejects a late sample from the old session");
            signal.Apply(sample with { ConnectionEpoch = current.ConnectionEpoch });
            Check(signal.Value == true, "the IO page model accepts a fresh sample from the new session");
        }
        if (secondIo is not null) await VerifySecondIoDevice(runtime, server, secondIo, io, monitor, grid, type);
    }

    private static async Task VerifySecondIoDevice(IPlcRuntime runtime, LoopbackOpcUa primary, LoopbackOpcUa secondary,
        FrameworkElement io, IoMonitorViewModel monitor, DataGrid grid, ComboBox type)
    {
        var deviceSelector = Walk<ComboBox>(io).Single(box => box.Items.Contains("PLC02"));
        var sample = secondary.SharedIo!;
        long? Epoch(string id) => runtime.Status.Devices.FirstOrDefault(device =>
            device.DeviceId == id && device.State == PlcDeviceState.Connected)?.ConnectionEpoch;
        bool NoWrites(FixtureNodes nodes) => nodes.ModeWrites().Length == 0 &&
            nodes.ParameterWrites().Length == 0 && nodes.WriteTrace().Rejected == 0;
        bool Shows(string device, string direction, bool[] values) =>
            monitor.SelectedDevice == device && SharedIoFixture.Matches(monitor, direction, values) &&
            monitor.Items[0].Key?.DeviceId == device;

        await Wait(() => Epoch("PLC02") is not null, "PLC02 establishes an independent OPC UA session");
        long primaryEpoch = Epoch("PLC01")!.Value;
        long secondaryEpoch = Epoch("PLC02")!.Value;
        var originalSecondary = secondary.Nodes;

        deviceSelector.SelectedItem = "PLC02";
        await Wait(() => Shows("PLC02", "DI", sample.DiBefore), "selecting PLC02 displays its own DI values");
        grid.SelectedIndex = 0;
        await Wait(() => Walk<TextBlock>(io).Any(text => text.Text == monitor.Items[0].BindingDetails &&
            text.Text.StartsWith("PLC02 | ", StringComparison.Ordinal)), "details show the selected PLC02 binding");
        type.SelectedItem = "DO";
        await Wait(() => Shows("PLC02", "DO", sample.DoBefore), "selecting PLC02 DO reads only the second device's outputs");
        type.SelectedItem = "DI";
        await Wait(() => Shows("PLC02", "DI", sample.DiBefore), "PLC02 DI history is established before disconnect");

        secondary.Disconnect();
        await Wait(() => Epoch("PLC02") is null && monitor.Items.All(row => row.Value is null) &&
            monitor.Items[0].LastValueText == sample.DiBefore[0].ToString() &&
            monitor.Items[1].LastValueText == sample.DiBefore[1].ToString(),
            "PLC02 disconnect invalidates only its current values and retains its own history");
        deviceSelector.SelectedItem = "PLC01";
        await Wait(() => Epoch("PLC01") == primaryEpoch && Shows("PLC01", "DI", primary.SharedIo!.DiAfter),
            "PLC01 still displays live data on the same session while PLC02 is offline");
        deviceSelector.SelectedItem = "PLC02";
        await Wait(() => monitor.Items[0].Key?.DeviceId == "PLC02" && monitor.Items.All(row => row.Value is null) &&
            monitor.Items[0].LastValueText == sample.DiBefore[0].ToString(),
            "switching back to the offline PLC02 does not borrow PLC01 values");

        await secondary.ConnectAsync(reconnected: true);
        await Wait(() => Epoch("PLC02") > secondaryEpoch && Shows("PLC02", "DI", sample.DiAfter),
            "PLC02 reconnect displays its new session's DI values");
        type.SelectedItem = "DO";
        await Wait(() => Shows("PLC02", "DO", sample.DoAfter), "PLC02 reconnect displays its new session's DO values");
        Check(Epoch("PLC01") == primaryEpoch && NoWrites(primary.Nodes) && NoWrites(originalSecondary) && NoWrites(secondary.Nodes),
            "device selections and the independent PLC02 reconnect produce no writes on either endpoint");
    }
}

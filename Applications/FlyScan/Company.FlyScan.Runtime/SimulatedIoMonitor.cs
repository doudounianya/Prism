using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using Company.Hmi.Contracts;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Company.FlyScan.Runtime;

/// <summary>主卡显示飞拍握手，第二台仿真卡用于独立 IO 分组演示。</summary>
public sealed class SimulatedIoMonitor : IIoMonitor, IDisposable
{
    private const uint BankCount = 2;
    private const string PrimaryDevice = "IMC-SIM-01";
    private const string SampleDevice = "IMC-SIM-02";
    private readonly SimulatedMachine _machine;
    private readonly NoneControlCard _sample = new();
    private readonly Dictionary<(string, string, uint, int), (bool Value, DateTimeOffset At)> _last = new();
    private string? _device = PrimaryDevice, _type = "DI", _group = "0";
    private bool _primaryConnected;
    private (FlyScanInputs Inputs, FlyScanOutputs Outputs)? _scanSnapshot;
    private DateTimeOffset _scanSampleAt;
    private bool _visible, _disposed;
    private IReadOnlyList<IIoSignal> _items = [];
    private string _summary = "", _message = "";

    public SimulatedIoMonitor(SimulatedMachine machine)
    {
        _machine = machine;
        _primaryConnected = machine.CardConnected;
        SetConnection(SampleDevice, connected: true);
        _machine.PropertyChanged += MachineChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Devices { get; } = [PrimaryDevice, SampleDevice];
    public IReadOnlyList<string> SignalTypes { get; } = ["DI", "DO"];
    public IReadOnlyList<string> Groups { get; } = ["0", "1"];
    /// <summary>采集飞拍主卡快照前读取；主卡断开或重连都会作废旧快照。</summary>
    public long ScanConnectionEpoch { get; private set; }
    public IReadOnlyList<IIoSignal> Items { get => _items; private set => Set(ref _items, value); }
    public string BindingLabel => "运动卡绑定";
    public string ReadOnlyNotice => "只读监视；ON / OFF 仅表示运动卡位值，不代表安全状态。";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? SelectedDevice
    {
        get => _device;
        set { if (value is not null && Devices.Contains(value) && Set(ref _device, value)) Refresh(); }
    }
    public string? SelectedSignalType
    {
        get => _type;
        set { if (value is not null && SignalTypes.Contains(value) && Set(ref _type, value)) Refresh(); }
    }
    public string? SelectedGroup
    {
        get => _group;
        set { if (value is not null && Groups.Contains(value) && Set(ref _group, value)) Refresh(); }
    }

    public Task SetViewVisibleAsync(bool visible)
    {
        if (_visible != visible && !_disposed)
        {
            _visible = visible;
            Refresh();
        }
        return Task.CompletedTask;
    }

    public void SetConnection(string device, bool connected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (device == PrimaryDevice)
        {
            _machine.SetCardConnected(connected);
            return;
        }
        if (device != SampleDevice) throw new ArgumentOutOfRangeException(nameof(device));
        if (connected && !_sample.IsInitialized)
        {
            if (!_sample.Initialize(new MotionConfiguration
            {
                Enabled = true,
                AllowNativeHardware = false,
                InputBankCount = BankCount,
                OutputBankCount = BankCount
            }))
                throw new InvalidOperationException("无法初始化运动卡仿真。");
            _sample.SetSimulatedInput(0, 1, true);
            _sample.SetSimulatedInput(1, 5, true);
        }
        else if (!connected)
            _sample.Close();
        Refresh();
    }

    private void MachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_primaryConnected == _machine.CardConnected) return;
        _primaryConnected = _machine.CardConnected;
        ++ScanConnectionEpoch;
        _scanSnapshot = null;
        if (SelectedDevice == PrimaryDevice) Refresh();
    }

    public void PublishScanIo(long connectionEpoch, FlyScanInputs inputs, FlyScanOutputs outputs)
    {
        if (_disposed || connectionEpoch != ScanConnectionEpoch) return;
        (FlyScanInputs Inputs, FlyScanOutputs Outputs)? snapshot =
            _machine.CardConnected && inputs.CardConnected ? (inputs, outputs) : null;
        if (_scanSnapshot == snapshot) return;
        _scanSnapshot = snapshot;
        _scanSampleAt = DateTimeOffset.UtcNow;
        if (SelectedDevice == PrimaryDevice) Refresh();
    }

    private (string Name, bool Value)[] ScanChannelValues(bool input)
    {
        var inputs = _scanSnapshot?.Inputs;
        var outputs = _scanSnapshot?.Outputs;
        if (input)
            return
            [
                ("上料确认", inputs?.LoadConfirmation is not null),
                ("机器人到位并保持", inputs?.Robot is { InPosition: true, Moving: false }),
                ("视觉 Ready", inputs?.VisionReady == true),
                ("下料确认", inputs?.UnloadConfirmation is not null),
                ("告警", inputs?.Alarm == true),
                ("急停", inputs?.EmergencyStop == true)
            ];
        return
        [
            ("上料请求", outputs?.RequestLoad is not null),
            ("机器人示教位请求", outputs?.RequestRobot is not null),
            ("机器人保持请求", outputs?.HoldRobot == true),
            ("视觉触发", outputs?.VisionTrigger == true),
            ("下料请求", outputs?.RequestUnload is not null)
        ];
    }

    private void Refresh()
    {
        if (!_visible || _disposed || SelectedDevice is not { } device ||
            SelectedSignalType is not { } type || !uint.TryParse(SelectedGroup, out var bank))
            return;

        bool primary = device == PrimaryDevice;
        bool connected = primary ? _machine.CardConnected : _sample.IsInitialized;
        bool input = type == "DI";
        bool read = connected && (primary ? _scanSnapshot.HasValue :
            input ? _sample.RefreshInputs(out _) : _sample.RefreshOutputs(out _));
        var scanPoints = primary && bank == 0 ? ScanChannelValues(input) : [];
        var points = read && !primary ? (input ? _sample.InputStates : _sample.OutputStates)
            .Where(p => p.Bank == bank).ToDictionary(p => p.Bit) : [];

        Items = Enumerable.Range(0, 16).Select(channel =>
        {
            bool configured = primary ? channel < scanPoints.Length : channel != 15;
            bool? value = null;
            if (configured && read)
            {
                if (primary) value = scanPoints[channel].Value;
                else if (points.GetValueOrDefault(channel) is { Available: true, Valid: true } point) value = point.Value;
            }
            var key = (device, type, bank, channel);
            if (value is { } valid)
                _last[key] = (valid, primary ? _scanSampleAt : points[channel].UpdatedAt);
            var historical = _last.TryGetValue(key, out var last) ? last : ((bool Value, DateTimeOffset At)?)null;
            string logical = $"IO.{type}[{bank}].{channel:00}";
            return (IIoSignal)new Signal(
                channel.ToString("00", CultureInfo.InvariantCulture),
                logical,
                !configured ? "未配置 / 禁用" : primary ? scanPoints[channel].Name : $"仿真{(input ? "输入" : "输出")}{channel:00}",
                value,
                value == true ? "ON" : value == false ? "OFF" : configured ? "未知" : "未配置 / 禁用",
                value == true ? IoIndicator.On : configured && value is null ? IoIndicator.ReadFailure : IoIndicator.Neutral,
                value.HasValue ? "Valid / Good" : configured ? "未知 · 等待当前连接读取" : "未配置 / 禁用",
                historical?.At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—",
                historical?.Value.ToString() ?? "—",
                configured ? $"{device} | {type} | 分组 {bank} | 通道 {channel:00}" : "未配置 / 禁用",
                configured && !read ? !connected ? "仿真连接已断开" : primary ? "等待飞拍当前反馈" : "仿真读取失败" : "");
        }).ToArray();
        int enabled = Items.Count(i => i.Value == true);
        int disabled = Items.Count(i => i.Value == false);
        Summary = $"ON {enabled}    OFF {disabled}    未知 / 未配置 {Items.Count - enabled - disabled}";
        Message = read ? $"正在监控 {device} / {type}[{bank}]，运动卡仿真"
            : $"等待 {device} / {type}[{bank}] 的有效仿真反馈。";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _machine.PropertyChanged -= MachineChanged;
        _sample.Close();
    }

    private sealed record Signal(string Channel, string LogicalId, string DisplayName, bool? Value, string StateText,
        IoIndicator Indicator, string QualityText, string UpdatedText, string LastValueText, string BindingDetails, string Error) : IIoSignal;
}

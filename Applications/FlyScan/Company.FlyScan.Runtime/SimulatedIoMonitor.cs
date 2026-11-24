using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using Company.Hmi.Contracts;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Company.FlyScan.Runtime;

/// <summary>G1 仿真卡 IO 适配器。所有读取和测试事件都由宿主界面线程串行处理。</summary>
public sealed class SimulatedIoMonitor : IIoMonitor, IDisposable
{
    private const uint BankCount = 2;
    private readonly Dictionary<string, NoneControlCard> _cards = new()
    {
        ["IMC-SIM-01"] = new(),
        ["IMC-SIM-02"] = new()
    };
    private readonly Dictionary<(string, string, uint, int), (bool Value, DateTimeOffset At)> _last = new();
    private string? _device = "IMC-SIM-01", _type = "DI", _group = "0";
    private bool _visible, _disposed;
    private IReadOnlyList<IIoSignal> _items = [];
    private string _summary = "", _message = "";

    public SimulatedIoMonitor()
    {
        Devices = _cards.Keys.ToArray();
        foreach (string device in Devices)
            SetConnection(device, connected: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Devices { get; }
    public IReadOnlyList<string> SignalTypes { get; } = ["DI", "DO"];
    public IReadOnlyList<string> Groups { get; } = ["0", "1"];
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
        var card = _cards[device];
        if (connected && !card.IsInitialized)
        {
            if (!card.Initialize(new MotionConfiguration
            {
                Enabled = true,
                AllowNativeHardware = false,
                InputBankCount = BankCount,
                OutputBankCount = BankCount
            }))
                throw new InvalidOperationException("无法初始化运动卡仿真。");
            card.SetSimulatedInput(0, device == Devices[0] ? 0 : 1, true);
            card.SetSimulatedInput(1, device == Devices[0] ? 4 : 5, true);
        }
        else if (!connected)
            card.Close();
        Refresh();
    }

    private void Refresh()
    {
        if (!_visible || _disposed || SelectedDevice is not { } device ||
            SelectedSignalType is not { } type || !uint.TryParse(SelectedGroup, out var bank))
            return;

        var card = _cards[device];
        bool connected = card.IsInitialized;
        bool input = type == "DI";
        bool read = connected && (input ? card.RefreshInputs(out _) : card.RefreshOutputs(out _));
        var points = read ? (input ? card.InputStates : card.OutputStates)
            .Where(p => p.Bank == bank).ToDictionary(p => p.Bit) : [];

        Items = Enumerable.Range(0, 16).Select(channel =>
        {
            // 每个仿真组的末位保留未映射，验证界面可区分未知、未配置与有效 OFF。
            bool configured = channel != 15;
            points.TryGetValue(channel, out var point);
            var key = (device, type, bank, channel);
            if (configured && point is { Available: true, Valid: true })
                _last[key] = (point.Value, point.UpdatedAt);
            var historical = _last.TryGetValue(key, out var last) ? last : ((bool Value, DateTimeOffset At)?)null;
            bool? value = configured && point is { Available: true, Valid: true } ? point.Value : null;
            string logical = $"IO.{type}[{bank}].{channel:00}";
            return (IIoSignal)new Signal(
                channel.ToString("00", CultureInfo.InvariantCulture),
                logical,
                configured ? $"仿真{(input ? "输入" : "输出")}{channel:00}" : "未配置 / 禁用",
                value,
                value == true ? "ON" : value == false ? "OFF" : configured ? "未知" : "未配置 / 禁用",
                value == true ? IoIndicator.On : configured && value is null ? IoIndicator.ReadFailure : IoIndicator.Neutral,
                value.HasValue ? "Valid / Good" : configured ? "未知 · 等待当前连接读取" : "未配置 / 禁用",
                historical?.At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—",
                historical?.Value.ToString() ?? "—",
                configured ? $"{device} | {type} | 分组 {bank} | 通道 {channel:00}" : "未配置 / 禁用",
                configured && !read ? connected ? "仿真读取失败" : "仿真连接已断开" : "");
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
        foreach (var card in _cards.Values) card.Close();
    }

    private sealed record Signal(string Channel, string LogicalId, string DisplayName, bool? Value, string StateText,
        IoIndicator Indicator, string QualityText, string UpdatedText, string LastValueText, string BindingDetails, string Error) : IIoSignal;
}

using System.ComponentModel;

namespace Company.Hmi.Contracts;

public enum IoIndicator
{
    Neutral,
    On,
    ReadFailure
}

public interface IIoSignal
{
    string Channel { get; }
    string LogicalId { get; }
    string DisplayName { get; }
    bool? Value { get; }
    string StateText { get; }
    IoIndicator Indicator { get; }
    string QualityText { get; }
    string UpdatedText { get; }
    string LastValueText { get; }
    string BindingDetails { get; }
    string Error { get; }
}

/// <summary>只读 IO 页面绑定。设备、连接代次和样本新鲜度由数据适配器管理。</summary>
public interface IIoMonitor : INotifyPropertyChanged
{
    IReadOnlyList<string> Devices { get; }
    IReadOnlyList<string> SignalTypes { get; }
    IReadOnlyList<string> Groups { get; }
    string? SelectedDevice { get; set; }
    string? SelectedSignalType { get; set; }
    string? SelectedGroup { get; set; }
    IReadOnlyList<IIoSignal> Items { get; }
    string BindingLabel { get; }
    string ReadOnlyNotice { get; }
    string Summary { get; }
    string Message { get; }
    Task SetViewVisibleAsync(bool visible);
}

/*
 * 文件职责与维护说明：
 * 设备监控的只读通讯状态概览，读取现有 IPlcRuntime 状态快照，不增加 PLC 业务采样。
 * 页面可见且处于导航活动状态时才运行 UI 刷新计时器；通讯正常不能推断设备处于可动作状态。
 */
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Windows.Threading;
namespace Company.Application.Main.ViewModels;

/// <summary>复用已有通讯状态快照，不增加 PLC 采样或推断设备运行模式。</summary>
public sealed class DeviceMonitorViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    private readonly IPlcRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private bool _active, _visible;
    // 集合与设备行常驻；状态轮询不替换 ItemsSource，保留表格选择、滚动位置及现有绑定。
    private readonly ObservableCollection<DeviceMonitorRow> _devices = [];
    private readonly Dictionary<string, DeviceMonitorRow> _deviceRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deviceIds = new(StringComparer.Ordinal);
    public IReadOnlyList<DeviceMonitorRow> Devices => _devices;
    public bool KeepAlive => true;
    public DeviceMonitorViewModel(IPlcRuntime runtime)
    {
        _runtime = runtime; _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }
    private void Refresh()
    {
        if (!_active || !_visible) return;
        // 仅读取同一份现有运行快照；设备清单变化时才增删或移动行，不增加通讯采样。
        var devices = _runtime.Status.Devices;
        _deviceIds.Clear();
        foreach (var device in devices) _deviceIds.Add(device.DeviceId);
        for (var index = _devices.Count - 1; index >= 0; index--)
        {
            var row = _devices[index];
            if (_deviceIds.Contains(row.Device)) continue;
            _deviceRows.Remove(row.Device);
            _devices.RemoveAt(index);
        }
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var state = device.State switch
            {
                PlcDeviceState.Connected => "通讯正常",
                PlcDeviceState.Connecting => "正在连接",
                PlcDeviceState.Faulted => "通讯异常，自动重连中",
                _ => "未连接"
            };
            var lastSuccess = device.LastCommunicationAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
            var error = device.Error ?? "";
            if (!_deviceRows.TryGetValue(device.DeviceId, out var row))
            {
                row = new DeviceMonitorRow(device.DeviceId, state, lastSuccess, error);
                _deviceRows.Add(device.DeviceId, row);
                _devices.Insert(index, row);
            }
            else
            {
                if (!ReferenceEquals(_devices[index], row)) _devices.Move(_devices.IndexOf(row), index);
                row.Update(state, lastSuccess, error);
            }
        }
    }
    public void SetVisible(bool visible) { _visible = visible; Observe(); }
    private void Observe() { if (_active && _visible) { Refresh(); _timer.Start(); } else _timer.Stop(); }
    public void OnNavigatedTo(NavigationContext context) { _active = true; Observe(); }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { _active = false; Observe(); }
}
/// <summary>设备标识保持不变；仅实际变化的显示字段发送属性通知。</summary>
public sealed class DeviceMonitorRow : ReactiveObject
{
    private string _state, _lastSuccess, _error;
    public string Device { get; }
    public string State { get => _state; private set => this.RaiseAndSetIfChanged(ref _state, value); }
    public string LastSuccess { get => _lastSuccess; private set => this.RaiseAndSetIfChanged(ref _lastSuccess, value); }
    public string Error { get => _error; private set => this.RaiseAndSetIfChanged(ref _error, value); }
    public DeviceMonitorRow(string Device, string State, string LastSuccess, string Error)
    {
        this.Device = Device; _state = State; _lastSuccess = LastSuccess; _error = Error;
    }
    internal void Update(string state, string lastSuccess, string error)
    {
        State = state; LastSuccess = lastSuccess; Error = error;
    }
}

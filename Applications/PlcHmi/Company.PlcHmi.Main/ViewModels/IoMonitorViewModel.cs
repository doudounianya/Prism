/*
 * 文件职责与维护说明：
 * IO 监控页按设备、DI/DO 类型和分组管理采集需求。
 * 分组行与最后有效值可复用，重新显示要等待本轮有效快照；页面定时器只读取内存，PLC 采样周期仍由采集组决定。
 */
using Company.Hmi.Contracts;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Logger;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Globalization;
using System.Windows.Threading;

namespace Company.PlcHmi.Main.ViewModels;

public sealed class IoMonitorViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime, IIoMonitor
{
    private readonly IPlcAcquisitionService _acquisition;
    private readonly IPlcDataProvider _data;
    private readonly IPlcBindingConfigProvider _configuration;
    private readonly IPlcRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly string _owner = "HMI.IO." + Guid.NewGuid().ToString("N");
    private IPlcAcquisitionScope? _scope;
    private CancellationTokenSource? _refresh;
    private bool _observing;
    private int _snapshotRefreshQueued;
    private bool _navigated, _viewVisible, _catalogLoading, _selectionReady;
    private long _revision;
    private Task? _queuedSelection;
    private (string? Stamp, string? Device, string? Type, string? Group)? _displayedSelection;
    private readonly Dictionary<(string? Device, string? Type, string? Group), IReadOnlyList<IoSignalItem>> _itemCache = new();
    private long _readEpoch;
    private string? _catalogStamp;
    private PlcRuntimeState? _catalogState;
    private IReadOnlyList<PlcVariableBinding> _bindings = [];
    private IReadOnlyList<string> _devices = [], _signalTypes = [], _groups = [];
    private string? _selectedDevice, _selectedSignalType, _selectedGroup;
    private IReadOnlyList<IoSignalItem> _items = [];
    // 键仅随显示范围重建；查找容器只复用容量，每轮仍从原数据提供者读取最新快照。
    private PlcVariableKey[] _snapshotKeys = [];
    private readonly Dictionary<PlcVariableKey, PlcVariableSnapshot> _snapshotLookup = new();
    private string _message = "请选择已启用的 IO 分组", _summary = "";
    public bool KeepAlive => true;
    public string BindingLabel => "PLC 绑定";
    public string ReadOnlyNotice => "只读监视；ON / OFF 仅表示 PLC 位值，不代表安全状态。";
    IReadOnlyList<IIoSignal> IIoMonitor.Items => Items;
    public IReadOnlyList<string> Devices { get => _devices; private set => this.RaiseAndSetIfChanged(ref _devices, value); }
    public IReadOnlyList<string> SignalTypes { get => _signalTypes; private set => this.RaiseAndSetIfChanged(ref _signalTypes, value); }
    public IReadOnlyList<string> Groups { get => _groups; private set => this.RaiseAndSetIfChanged(ref _groups, value); }
    public string? SelectedDevice { get => _selectedDevice; set { if (_selectedDevice == value) return; this.RaiseAndSetIfChanged(ref _selectedDevice, value); if (!_catalogLoading) { LoadOptions(); QueueSelection(); } } }
    public string? SelectedSignalType { get => _selectedSignalType; set { if (_selectedSignalType == value) return; this.RaiseAndSetIfChanged(ref _selectedSignalType, value); if (!_catalogLoading) { LoadOptions(); QueueSelection(); } } }
    public string? SelectedGroup { get => _selectedGroup; set { if (_selectedGroup == value) return; this.RaiseAndSetIfChanged(ref _selectedGroup, value); if (!_catalogLoading) QueueSelection(); } }
    public IReadOnlyList<IoSignalItem> Items { get => _items; private set => this.RaiseAndSetIfChanged(ref _items, value); }
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    public IoMonitorViewModel(IPlcAcquisitionService acquisition, IPlcDataProvider data,
        IPlcBindingConfigProvider configuration, IPlcRuntime runtime)
    {
        _acquisition = acquisition; _data = data; _configuration = configuration; _runtime = runtime;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            try { LoadCatalog(); RefreshSnapshots(); }
            catch (Exception ex) { Message = "IO 内存快照读取失败：" + ex.Message; Logs.LogError(ex); }
        };
    }
    private static string Meta(PlcVariableBinding v, string name) => v.Metadata.TryGetValue(name, out var value) ? value : "";
    /// <summary>只构造当前默认分组及缓存行，不申请采集、不读取快照。</summary>
    public void PrepareDisplay()
    {
        if (_navigated || _viewVisible) return;
        LoadCatalog();
        PrepareItems(_bindings.Where(v => v.DeviceId == SelectedDevice &&
            Meta(v, "信号类型") == SelectedSignalType && Meta(v, "分组号") == SelectedGroup).ToArray());
    }
    // 只在已导航且实际显示时订阅内存变化并启动 UI 刷新；后台报警/产能消费者不受影响。
    private void UpdateObservation()
    {
        bool active = _navigated && _viewVisible;
        if (_observing == active) return;
        _observing = active;
        if (active) { _data.ValuesChanged += ValuesChanged; _timer.Start(); }
        else { _data.ValuesChanged -= ValuesChanged; _timer.Stop(); }
    }
    private void ValuesChanged(object? sender, EventArgs args)
    {
        var dispatcher = _timer.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || Interlocked.Exchange(ref _snapshotRefreshQueued, 1) != 0) return;
        // 首读结果仍立即排队，不等兜底计时器；刷新让出输入和渲染，避免持续通知饿死导航点击。
        dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _snapshotRefreshQueued, 0);
            if (!_observing) return;
            try { LoadCatalog(); RefreshSnapshots(); }
            catch (Exception ex) { Message = "IO 内存快照读取失败：" + ex.Message; Logs.LogError(ex); }
        }, DispatcherPriority.Background);
    }
    // 按当前配置内容戳生成 IO 目录，DI/DO 及分组取自元数据。
    // 仅在配置变化时清空行缓存；运行状态变化只重新申请当前范围。
    private void LoadCatalog()
    {
        var config = _configuration.Current;
        var stamp = config is null ? null : config.Version + ":" + config.FileSha256;
        var runtimeState = _runtime.Status.State;
        if (_catalogStamp == stamp && _catalogState == runtimeState) return;
        bool configurationChanged = _catalogStamp != stamp;
        _catalogStamp = stamp;
        _catalogState = runtimeState;
        if (!configurationChanged) { QueueSelection(); return; }
        _itemCache.Clear(); _displayedSelection = null; Items = []; _snapshotKeys = []; _snapshotLookup.Clear();
        _bindings = config?.ActiveVariables.Where(v => v.Category == "IO" &&
            v.Access.HasFlag(PlcAccess.Read) && Meta(v, "信号类型") is "DI" or "DO").ToArray() ?? [];
        _catalogLoading = true;
        Devices = _bindings.Select(v => v.DeviceId).Distinct().Order().ToArray();
        SelectedDevice = Devices.Contains(SelectedDevice!) ? SelectedDevice : Devices.FirstOrDefault();
        _catalogLoading = false;
        LoadOptions(); QueueSelection();
    }
    private void LoadOptions()
    {
        _catalogLoading = true;
        var device = _bindings.Where(v => v.DeviceId == SelectedDevice).ToArray();
        SignalTypes = device.Select(v => Meta(v, "信号类型")).Distinct().Order().ToArray();
        SelectedSignalType = SignalTypes.Contains(SelectedSignalType!) ? SelectedSignalType : SignalTypes.FirstOrDefault();
        Groups = device.Where(v => Meta(v, "信号类型") == SelectedSignalType)
            .Select(v => Meta(v, "分组号")).Where(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Distinct().OrderBy(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        SelectedGroup = Groups.Contains(SelectedGroup!) ? SelectedGroup : Groups.FirstOrDefault();
        _catalogLoading = false;
    }
    // 选择变化先取消旧刷新并标记当前行是缓存，再合并排队新的范围申请。
    // 界面保持已有行实例，避免切换时先清空再重建造成闪烁。
    private Task QueueSelection()
    {
        ++_revision;
        _refresh?.Cancel();
        _selectionReady = false;
        foreach (var item in Items) item.MarkCached();
        return _queuedSelection ??= _timer.Dispatcher.InvokeAsync(async () =>
        {
            _queuedSelection = null;
            await ObserveSelectionAsync();
        }, DispatcherPriority.Loaded).Task.Unwrap();
    }
    private async Task ObserveSelectionAsync()
    {
        try { await ApplySelectionAsync(); }
        catch (Exception ex) { Message = "IO 采集申请失败：" + ex.Message; Logs.LogError(ex); }
    }
    /// <summary>所有选择变更串行生效；过期请求只能释放/替换旧范围，不能恢复隐藏页采集。</summary>
    // 选择变更通过一个门闩串行应用，代次检查阻止过期请求恢复隐藏页采集。
    // 隐藏、无绑定或运行未启动时释放页面 scope；显示时只申请当前设备/类型/组的键。
    public async Task ApplySelectionAsync()
    {
        long revision = ++_revision;
        _selectionReady = false;
        await _selectionGate.WaitAsync();
        try
        {
            if (revision != _revision) return;
            bool active = _navigated && _viewVisible;
            var selected = active ? _bindings.Where(v => v.DeviceId == SelectedDevice &&
                Meta(v, "信号类型") == SelectedSignalType && Meta(v, "分组号") == SelectedGroup).ToArray() : [];
            if (active) PrepareItems(selected);
            if (!active || selected.Length == 0 || _runtime.Status.State != PlcRuntimeState.Running)
            {
                if (_scope is not null) { var old = _scope; _scope = null; await Task.Run(async () => await old.DisposeAsync()); }
                foreach (var item in Items) item.MarkCached(active ? "采集未启动" : "页面未显示");
                Message = active ? "当前没有可采集的已启用 IO 绑定，或 PLC 运行服务尚未运行。" : "页面已隐藏，IO 采集申请已释放。";
                Summary = "";
                return;
            }
            var keys = selected.Select(v => new PlcVariableKey(v.Protocol, v.DeviceId, v.LogicalId)).ToArray();
            var request = new PlcAcquisitionRequest(_owner, selected.Select(v => v.AcquisitionGroup).Distinct().ToArray(), keys);
            var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice);
            _readEpoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
            var requestedAt = DateTimeOffset.UtcNow;
            foreach (var item in Items) item.BeginRefresh(_readEpoch, _catalogStamp ?? "", requestedAt);
            await Task.Run(async () =>
            {
                if (_scope is null) _scope = await _acquisition.OpenPageScopeAsync(request).ConfigureAwait(false);
                else
                {
                    try { await _scope.UpdateAsync(request).ConfigureAwait(false); }
                    catch (ObjectDisposedException)
                    {
                        await _scope.DisposeAsync().ConfigureAwait(false);
                        _scope = null;
                        if (revision != _revision) return;
                        _scope = await _acquisition.OpenPageScopeAsync(request).ConfigureAwait(false);
                    }
                }
            });
            if (revision != _revision) return; // 后续排队操作负责清理或替换，旧结果不显示。
            _selectionReady = true;
            Message = $"正在监控 {SelectedDevice} / {SelectedSignalType}[{SelectedGroup}]；仅当前组，DO 仅监视。";
            RefreshSnapshots();
        }
        catch
        {
            var scope = _scope; _scope = null;
            foreach (var item in Items) item.MarkCached("采集申请失败");
            Summary = "当前分组采集申请失败，显示缓存";
            if (scope is not null) await Task.Run(async () => await scope.DisposeAsync());
            throw;
        }
        finally { _selectionGate.Release(); }
    }
    // 一组固定创建 16 个通道；缺少绑定的通道仍保留“未配置”占位。
    // 缓存键包含设备、信号类型和组号，不能把不同组的信号值串用。
    private void PrepareItems(IReadOnlyList<PlcVariableBinding> selected)
    {
        var selection = (_catalogStamp, SelectedDevice, SelectedSignalType, SelectedGroup);
        if (_displayedSelection == selection) return;
        if (selected.Count == 0) { Items = []; _snapshotKeys = []; _snapshotLookup.Clear(); _displayedSelection = selection; return; }
        var key = (SelectedDevice, SelectedSignalType, SelectedGroup);
        if (!_itemCache.TryGetValue(key, out var items))
        {
            items = Enumerable.Range(0, 16).Select(channel =>
            {
                string id = $"IO.{SelectedSignalType}[{SelectedGroup}].{channel:00}";
                var binding = selected.FirstOrDefault(v => v.LogicalId == id);
                return new IoSignalItem { Channel = channel.ToString("00"), LogicalId = id,
                    DisplayName = binding?.DisplayName ?? "未配置 / 禁用",
                    Key = binding is null ? null : new(binding.Protocol, binding.DeviceId, binding.LogicalId),
                    BindingDetails = binding?.Address is OpcUaBindingAddress address ?
                        $"{binding.DeviceId} | {address.NamespaceUri} | {address.NodeIdentifier} | 数组索引 {address.ArrayIndex}" :
                        binding?.Address.ToString() ?? "未配置 / 禁用" };
            }).ToArray();
            _itemCache[key] = items;
        }
        Items = items; _snapshotKeys = items.Where(i => i.Key is not null).Select(i => i.Key!).ToArray();
        _snapshotLookup.Clear(); _displayedSelection = selection;
    }
    // 这里只读取 IPlcDataProvider 的内存快照，不发起网络读取。
    // 若运行状态或连接代次改变，重新建立本轮需求及新鲜度门槛。
    public void RefreshSnapshots()
    {
        if (!_navigated || !_viewVisible || !_selectionReady || Items.Count == 0) return;
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice);
        var epoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
        if (_runtime.Status.State != PlcRuntimeState.Running || epoch != _readEpoch) { QueueSelection(); return; }
        _snapshotLookup.Clear();
        // Add 保留原 ToDictionary 对重复键的拒绝行为，缺失键不会沿用上轮值。
        foreach (var snapshot in _data.GetSnapshots(_snapshotKeys)) _snapshotLookup.Add(snapshot.Key, snapshot);
        foreach (var item in Items) item.Apply(item.Key is not null && _snapshotLookup.TryGetValue(item.Key, out var value) ? value : null);
        Summary = $"ON {Items.Count(i => i.Value == true)}    OFF {Items.Count(i => i.Value == false)}    未知 / 未配置 {Items.Count(i => i.Value is null)}    UI 从内存刷新；PLC 周期使用 Excel 采集组配置";
    }
    // 手动刷新转交 scope 执行，并允许后续切组或离页取消。
    // 刷新完成只在页面代次仍相同时更新界面，避免迟到结果覆盖新分组。
    public async Task RefreshAsync()
    {
        long revision = _revision;
        using var refresh = new CancellationTokenSource();
        Task? pending = null;
        await _selectionGate.WaitAsync();
        try
        {
            _refresh?.Cancel(); _refresh = refresh;
            if (_navigated && _viewVisible && _scope is { } scope)
                pending = Task.Run(() => scope.RefreshAsync(refresh.Token));
        }
        finally { _selectionGate.Release(); }
        try { if (pending is not null) await pending; }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (revision != _revision) { }
        finally { if (ReferenceEquals(_refresh, refresh)) _refresh = null; }
        if (revision == _revision) RefreshSnapshots();
    }
    public Task SetViewVisibleAsync(bool visible)
    {
        if (_viewVisible == visible) return _queuedSelection ?? Task.CompletedTask;
        _viewVisible = visible;
        if (_navigated && visible) LoadCatalog();
        UpdateObservation();
        return QueueSelection();
    }
    public void OnNavigatedTo(NavigationContext navigationContext) { _navigated = true; LoadCatalog(); UpdateObservation(); QueueSelection(); }
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) { _navigated = false; UpdateObservation(); QueueSelection(); }
}

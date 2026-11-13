/*
 * 文件职责与维护说明：
 * 工艺参数的只读页面，依据设备/上下文/配方/阶段/类别筛选字段，并缓存同一范围的显示对象。
 * 该文件不提供 PLC 参数下发；可见性和选择代次共同决定哪些采集结果可以应用。
 */
using Company.Application.Main.Models;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Logger;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Windows.Threading;

namespace Company.Application.Main.ViewModels;

public sealed class ProcessParameterViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    private readonly ProcessBindingCatalog _catalog;
    private readonly ComponentPageSession _session;
    private readonly IPlcDataProvider _data;
    private readonly IPlcRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private bool _navigated, _visible, _loading, _pageReady;
    private long _revision;
    private Task? _queuedSelection;
    private (string Stamp, string? Device, string? Context, string? Recipe, string? Phase, string? Category)? _preparedFor;
    private readonly Dictionary<(string Stamp, string? Device, string? Context, string? Recipe, string? Phase, string? Category),
        (IReadOnlyList<PlcDisplayField> Page, IReadOnlyList<PlcDisplayField> Common)> _fieldCache = new();
    private long _readEpoch;
    private string? _stamp;
    private string _expectedStamp = "";
    private PlcRuntimeState? _state;
    private IReadOnlyList<ComponentOption> _devices = [];
    private IReadOnlyList<string> _contexts = [], _recipes = [], _phases = [], _categories = [];
    private ComponentOption? _device;
    private string? _context, _recipe, _phase, _category;
    private IReadOnlyList<PlcDisplayField> _pageFields = [], _commonFields = [];
    // 公共字段键仅随选择/配置重建；两类快照各保留一个查找容器，刷新不再重复分配字典。
    private PlcVariableKey[] _commonSnapshotKeys = [];
    private readonly Dictionary<PlcVariableKey, PlcVariableSnapshot> _pageSnapshotLookup = new(), _commonSnapshotLookup = new();
    private ProcessParameterSelection? _selection;
    private string _message = "采集未启动", _summary = "", _diagnostics = "";

    public ProcessParameterViewModel(ProcessBindingCatalog catalog, ComponentPageSession session,
        IPlcDataProvider data, IPlcRuntime runtime)
    {
        _catalog = catalog; _session = session; _data = data; _runtime = runtime;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { CheckConfiguration(); RefreshSnapshots(); };
    }
    public bool KeepAlive => true;
    /// <summary>只预建默认配方字段；页面通讯仍等待真实导航和可见状态。</summary>
    public void PrepareDisplay()
    {
        if (_navigated || _visible) return;
        CheckConfiguration();
        PrepareFields();
    }
    public IReadOnlyList<ComponentOption> Devices { get => _devices; private set => this.RaiseAndSetIfChanged(ref _devices, value); }
    public IReadOnlyList<string> Contexts { get => _contexts; private set => this.RaiseAndSetIfChanged(ref _contexts, value); }
    public IReadOnlyList<string> Recipes { get => _recipes; private set => this.RaiseAndSetIfChanged(ref _recipes, value); }
    public IReadOnlyList<string> Phases { get => _phases; private set => this.RaiseAndSetIfChanged(ref _phases, value); }
    public IReadOnlyList<string> Categories { get => _categories; private set => this.RaiseAndSetIfChanged(ref _categories, value); }
    public ComponentOption? SelectedDevice { get => _device; set { if (_device == value) return; this.RaiseAndSetIfChanged(ref _device, value); SelectionChanged(); } }
    public string? SelectedContext { get => _context; set { if (_context == value) return; this.RaiseAndSetIfChanged(ref _context, value); SelectionChanged(); } }
    public string? SelectedRecipe { get => _recipe; set { if (_recipe == value) return; this.RaiseAndSetIfChanged(ref _recipe, value); SelectionChanged(); } }
    public string? SelectedPhase { get => _phase; set { if (_phase == value) return; this.RaiseAndSetIfChanged(ref _phase, value); SelectionChanged(); } }
    public string? SelectedCategory { get => _category; set { if (_category == value) return; this.RaiseAndSetIfChanged(ref _category, value); SelectionChanged(); } }
    public ProcessParameterSelection? Selection { get => _selection; private set => this.RaiseAndSetIfChanged(ref _selection, value); }
    public IReadOnlyList<PlcDisplayField> PageFields { get => _pageFields; private set => this.RaiseAndSetIfChanged(ref _pageFields, value); }
    public IReadOnlyList<PlcDisplayField> CommonFields { get => _commonFields; private set => this.RaiseAndSetIfChanged(ref _commonFields, value); }
    public PlcDisplayField? ActiveRecipe => CommonFields.FirstOrDefault(f => f.LogicalId.EndsWith(".ActiveRecipe", StringComparison.Ordinal));
    public PlcDisplayField? ActiveRevision => CommonFields.FirstOrDefault(f => f.LogicalId.EndsWith(".ActiveRevision", StringComparison.Ordinal));
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }
    public string Diagnostics { get => _diagnostics; private set => this.RaiseAndSetIfChanged(ref _diagnostics, value); }
    public bool CanRefresh => _navigated && _visible && _pageReady && _runtime.Status.State == PlcRuntimeState.Running;

    private static string? Retain(IReadOnlyList<string> options, string? selected) => selected is not null && options.Contains(selected)
        ? selected : options.FirstOrDefault();
    private void SelectionChanged()
    {
        if (_loading) return;
        LoadOptions(); QueueSelection();
    }
    // 筛选项按设备→上下文→配方→阶段→类别逐级收窄，仍有效的选择优先保留。
    // _loading 阻止程序更新下拉框时递归触发多次采集申请。
    private void LoadOptions()
    {
        _loading = true;
        try
        {
            Contexts = _catalog.Contexts(SelectedDevice?.Id);
            SelectedContext = Retain(Contexts, SelectedContext);
            Recipes = _catalog.Recipes(SelectedDevice?.Id, SelectedContext);
            SelectedRecipe = Retain(Recipes, SelectedRecipe);
            Phases = _catalog.Phases(SelectedDevice?.Id, SelectedContext, SelectedRecipe);
            SelectedPhase = Retain(Phases, SelectedPhase);
            Categories = _catalog.Categories(SelectedDevice?.Id, SelectedContext, SelectedRecipe, SelectedPhase);
            SelectedCategory = Retain(Categories, SelectedCategory);
        }
        finally { _loading = false; }
    }
    private void CheckConfiguration()
    {
        var state = _runtime.Status.State;
        if (_stamp == _catalog.Stamp && _state == state) return;
        if (_stamp != _catalog.Stamp) { _fieldCache.Clear(); _preparedFor = null; }
        _stamp = _catalog.Stamp; _state = state;
        _loading = true;
        Devices = _catalog.Devices;
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == SelectedDevice?.Id) ?? Devices.FirstOrDefault();
        _loading = false;
        LoadOptions(); QueueSelection();
    }
    private ProcessParameterSelection? Capture() => SelectedDevice is not null && SelectedContext is not null
        && SelectedRecipe is not null && SelectedPhase is not null && SelectedCategory is not null
        ? new(SelectedDevice.Id, _catalog.Protocol(SelectedDevice.Id), SelectedContext, SelectedRecipe, SelectedPhase, SelectedCategory) : null;
    // 取消/替换当前选择的准备工作，并把短时间多次筛选变化合并成一次排队处理。
    private Task QueueSelection()
    {
        ++_revision;
        _pageReady = false;
        this.RaisePropertyChanged(nameof(CanRefresh));
        return _queuedSelection ??= _timer.Dispatcher.InvokeAsync(async () =>
        {
            _queuedSelection = null;
            await ObserveSelectionAsync();
        }, DispatcherPriority.Background).Task.Unwrap();
    }
    private async Task ObserveSelectionAsync()
    {
        try { await ApplySelectionAsync(); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    // 申请范围以当前不可变选择快照为准；用代次排除旧异步结果。
    // 仅活动且可见时设置页面 scope，隐藏时释放需求，不能把布局预热当作真实打开。
    public async Task ApplySelectionAsync()
    {
        long revision = ++_revision;
        _pageReady = false;
        this.RaisePropertyChanged(nameof(CanRefresh));
        if (!_navigated || !_visible)
        {
            Message = "页面已隐藏，工艺参数采集需求已释放";
            await _session.SetAsync([], false);
            return;
        }
        PrepareFields();
        var fields = PageFields.Concat(CommonFields.Where(f => _catalog.Configuration?.AcquisitionGroups
            .Any(g => g.Id == f.Binding.AcquisitionGroup && g.Lifetime == Company.Application.Share.PLC.Configuration.PlcAcquisitionLifetime.Page) == true)).ToArray();
        bool active = _navigated && _visible && _runtime.Status.State == PlcRuntimeState.Running;
        Message = !_navigated || !_visible ? "页面已隐藏，工艺参数采集需求已释放"
            : !active ? "采集未启动 · 不自动连接 PLC"
            : Selection is null ? "没有可识别的配方／阶段配置"
            : !fields.Any(f => f.CanRead) ? "当前阶段没有已启用且地址完整的可读绑定"
            : "等待当前阶段及公共信息读取";
        Summary = Selection is { } s
            ? $"{s.DeviceId} / {s.Context} / {s.Recipe} / {s.Phase} / {s.ParameterCategory} · {_catalog.ReadMode(fields)}"
            : "请检查设备及工艺参数绑定；不提供编辑、应用或发布操作";
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice?.Id);
        _readEpoch = active && device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
        var requestedAt = DateTimeOffset.UtcNow;
        foreach (var f in PageFields.Concat(CommonFields))
            if (active) f.BeginRefresh(_readEpoch, _expectedStamp, requestedAt);
            else f.MarkCached("采集未启动");
        try
        {
            // 新配置的公共信息加入当前页面范围；旧配置的后台字段继续只读缓存，避免重复采集。
            await _session.SetAsync(fields, active);
            if (revision != _revision) return;
            _pageReady = active && fields.Any(f => f.CanRead);
            if (_pageReady) Message = $"当前阶段及公共信息 {fields.Count(f => f.CanRead)} 个可读参数 · 仅当前显示范围";
            this.RaisePropertyChanged(nameof(CanRefresh));
            RefreshSnapshots();
        }
        catch (Exception ex)
        {
            if (revision == _revision)
            {
                foreach (var f in fields) f.Apply(null, "采集申请失败");
                Message = "工艺参数采集申请失败：" + ex.Message;
            }
            throw;
        }
    }
    // 对同一个配置/设备/配方筛选范围复用字段集合，避免每次刷新重建控件。
    private void PrepareFields()
    {
        Selection = Capture();
        _expectedStamp = _catalog.Stamp;
        var selection = (_expectedStamp, SelectedDevice?.Id, SelectedContext, SelectedRecipe, SelectedPhase, SelectedCategory);
        if (_preparedFor == selection) return;
        if (!_fieldCache.TryGetValue(selection, out var cached))
            _fieldCache[selection] = cached = (_catalog.PageFields(Selection), _catalog.CommonFields(SelectedDevice?.Id, SelectedContext));
        PageFields = cached.Page; CommonFields = cached.Common;
        _commonSnapshotKeys = CommonFields.Where(f => f.CanRead).Select(f => f.Key).Distinct().ToArray();
        _pageSnapshotLookup.Clear(); _commonSnapshotLookup.Clear();
        this.RaisePropertyChanged(nameof(ActiveRecipe));
        this.RaisePropertyChanged(nameof(ActiveRevision));
        Diagnostics = _catalog.Diagnostics(SelectedDevice?.Id);
        _preparedFor = selection;
    }
    private void ApplySnapshot(PlcDisplayField field, PlcVariableSnapshot? value)
    {
        if (value is not null && value.ConfigurationVersion != _expectedStamp)
            field.Apply(null, "配置版本不一致，等待重新采集");
        else field.Apply(value);
    }
    // 从内存缓存投影当前字段，验证当前配置及连接代次；显示缓存不能提升为最新有效数据。
    public void RefreshSnapshots()
    {
        if (!_navigated || !_visible) return;
        if (_runtime.Status.State != PlcRuntimeState.Running)
        {
            foreach (var f in PageFields.Concat(CommonFields)) f.MarkCached("采集未启动");
            return;
        }
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice?.Id);
        var epoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
        if (_pageReady && epoch != _readEpoch) { QueueSelection(); return; }
        if (_pageReady)
        {
            try
            {
                _pageSnapshotLookup.Clear();
                foreach (var snapshot in _session.Snapshots()) _pageSnapshotLookup.Add(snapshot.Key, snapshot);
                foreach (var f in PageFields) ApplySnapshot(f, _pageSnapshotLookup.GetValueOrDefault(f.Key));
            }
            catch (Exception ex)
            {
                foreach (var f in PageFields) f.Apply(null, "页面快照不可用");
                Message = "工艺参数内存快照读取失败：" + ex.Message; Logs.LogError(ex);
            }
        }
        try
        {
            _commonSnapshotLookup.Clear();
            if (_commonSnapshotKeys.Length > 0)
                foreach (var snapshot in _data.GetSnapshots(_commonSnapshotKeys)) _commonSnapshotLookup.Add(snapshot.Key, snapshot);
            foreach (var f in CommonFields) ApplySnapshot(f, _commonSnapshotLookup.GetValueOrDefault(f.Key));
        }
        catch (Exception ex)
        {
            foreach (var f in CommonFields) f.Apply(null, "后台缓存不可用");
            Logs.LogError(ex);
        }
    }
    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        long revision = _revision;
        try { await _session.RefreshAsync(); if (revision == _revision) RefreshSnapshots(); }
        catch (Exception ex)
        {
            if (revision == _revision)
            {
                foreach (var f in PageFields) f.Apply(null, "刷新失败");
                Message = "当前阶段刷新失败：" + ex.Message;
            }
            Logs.LogError(ex);
        }
    }
    public Task SetViewVisibleAsync(bool visible)
    {
        if (_visible == visible) return _queuedSelection ?? Task.CompletedTask;
        _visible = visible;
        if (_navigated && visible) { CheckConfiguration(); _timer.Start(); } else _timer.Stop();
        return QueueSelection();
    }
    public void OnNavigatedTo(NavigationContext context) { _navigated = true; CheckConfiguration(); if (_visible) _timer.Start(); QueueSelection(); }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { _navigated = false; _timer.Stop(); QueueSelection(); }
}

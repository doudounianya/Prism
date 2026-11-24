using Company.Hmi.Contracts;
using Company.PlcHmi.Main.Views;
/*
 * 文件职责与维护说明：
 * 轴与气缸共享的页面生命周期及轴专用显示逻辑。
 * 页面对象和行可以常驻，但只有已导航且可见时申请当前范围；写入还须满足身份、连接、配置、数据新鲜度和页面版本条件。
 */
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.Services.PLC;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Logger;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Windows.Threading;
using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Shared.PLC.Configuration;

namespace Company.PlcHmi.Main.ViewModels;

/// <summary>业务页面共同生命周期，不改变框架的导航或采集接口。</summary>
public abstract class ComponentParameterViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime, IParameterPage
{
    protected readonly ComponentBindingCatalog Catalog;
    private readonly ComponentPageSession _session;
    private readonly IPlcRuntime _runtime;
    private readonly ComponentOperationSession _operations;
    private Task _release = Task.CompletedTask;
    private bool _busy, _hasFreshData, _controlsAllowed, _operateAllowed;
    // 与页面选择代次分开：一次按下和一次松开也会推进它，用来丢弃迟到的按钮置位显示结果。
    private long _pressRevision;
    private (PlcWriteContext Context, long Revision, string Suffix)? _heldDisplay;
    private bool _observing;
    private int _snapshotRefreshQueued;
    private string _operationMessage;
    private readonly DispatcherTimer _timer;
    private readonly string _category;
    // _selectionReady 表示范围申请已生效；是否已有本轮有效值还需看 _hasFreshData，二者不能混用。
    private bool _navigated, _visible, _loading, _selectionReady;
    // 页面选择代次：设备、构件、子页、可见性变化时递增。
    // 异步申请保存开始时的代次，返回后再比较，防止旧设备结果覆盖新选择。
    private long _revision;
    private Task? _queuedSelection;
    private (string? Stamp, string? Device, string? Component, int Tab, int Page)? _preparedFor;
    // 字段缓存按设备/构件保留绑定对象；页面缓存再按子页和分页保留行。
    // 配置版本变化会整体清理缓存，不能让旧 Excel 的地址继续被使用。
    private readonly Dictionary<(string? Device, string? Component), IReadOnlyList<PlcDisplayField>> _fieldCache = new();
    private sealed record PreparedPage(IReadOnlyList<PlcDisplayField> Fields, IReadOnlyList<PlcDisplayField> Header,
        IReadOnlyList<PlcDisplayField> Feedback, IReadOnlyList<PlcDisplayField> Details, IReadOnlyList<PlcDisplayField> Visible, object? Rows);
    private readonly Dictionary<(string? Device, string? Component, int Tab, int Page), PreparedPage> _pageCache = new();
    // 本页面读取所属的连接代次；重连后即使设备编号相同，也必须建立新的一轮读取来源。
    private long _readEpoch;
    private string? _stamp;
    private PlcRuntimeState? _state;
    private IReadOnlyList<ComponentOption> _devices = [], _components = [];
    private ComponentOption? _device, _component;
    private int _tab, _page;
    private IReadOnlyList<PlcDisplayField> _fields = [], _header = [], _feedback = [], _details = [], _visibleFields = [];
    // 只复用查找容器，不缓存样本有效性；每次刷新仍清空并装入当前页面会话的最新快照。
    private readonly Dictionary<PlcVariableKey, PlcVariableSnapshot> _snapshotLookup = new();
    private string _message = "采集未启动";
    private readonly ModeControlViewModel? _modeControl;
    private const string ReadOnlyHint = "只读查看；设备工程师、工艺工程师或管理员须在手动调试状态下才能修改参数和操作。";
    protected ComponentParameterViewModel(string category, ComponentBindingCatalog catalog, ComponentPageSession session, IPlcRuntime runtime, ComponentOperationSession operations, ModeControlViewModel? modeControl = null)
    {
        _category = category; Catalog = catalog; _session = session; _runtime = runtime; _operations = operations;
        _modeControl = modeControl;
        _operationMessage = operations.Enabled ? ReadOnlyHint : "实机写入未启用";
        // 初始子页与已确认预览一致；原有采集分区编号保持不变。
        _tab = category == "轴" ? 3 : 1;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { CheckConfiguration(); RefreshSnapshots(); };
        operations.PermissionsChanged += (_, _) =>
        {
            void Update()
            {
                ++_pressRevision; _heldDisplay = null;
                UpdateWriteState();
                OperationMessage = operations.CanEditParameters ? "权限已更新，等待当前页有效读取后可修改参数及操作构件。" : ReadOnlyHint;
            }
            if (_timer.Dispatcher.CheckAccess()) Update(); else _timer.Dispatcher.InvokeAsync(Update);
        };
    }
    public bool KeepAlive => true;
    /// <summary>预建当前构件默认子页的字段和行；隐藏状态不开放操作或页面采集。</summary>
    public void PrepareDisplay()
    {
        if (_navigated || _visible) return;
        CheckConfiguration();
        var selection = (_stamp, SelectedDevice?.Id, SelectedComponent?.Id, SelectedTab, PositionPage);
        if (_preparedFor != selection) { Prepare(); _preparedFor = selection; }
    }
    // 导航激活与实际可见必须同时满足才观察快照。
    // 此处启动的 UI 计时器用于读取内存显示，不等同于 PLC 的采样周期。
    private void UpdateObservation()
    {
        bool active = _navigated && _visible;
        if (_observing == active) return;
        _observing = active;
        if (active) { _session.ValuesChanged += ValuesChanged; _timer.Start(); }
        else { _session.ValuesChanged -= ValuesChanged; _timer.Stop(); }
    }
    private void ValuesChanged(object? sender, EventArgs args)
    {
        var dispatcher = _timer.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || Interlocked.Exchange(ref _snapshotRefreshQueued, 1) != 0) return;
        // 数据到达即排队，但输入和页面显示优先；后台报警通知不能阻塞页面切换。
        dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _snapshotRefreshQueued, 0);
            if (!_observing) return;
            try { CheckConfiguration(); RefreshSnapshots(); }
            catch (Exception ex) { Message = "内存快照读取失败：" + ex.Message; Logs.LogError(ex); }
        }, DispatcherPriority.Background);
    }
    // 控件可用性不随其他命令的短暂忙状态切换；发送入口仍用CanOperate防止并发操作。
    public bool CanOperate => !_busy && CanUseControls;
    public bool CanUseControls => _navigated && _visible && _selectionReady && _operations.Enabled && _operations.CanEditParameters &&
        _runtime.Status.State == PlcRuntimeState.Running && _runtime.Status.ConfigurationVersion == _stamp &&
        _runtime.Status.Devices.Any(d => d.DeviceId == SelectedDevice?.Id && d.State == PlcDeviceState.Connected && d.ConnectionEpoch == _readEpoch) && _hasFreshData;
    public string WriteModeText => (_operations.Enabled ? _operations.CanEditParameters ? "参数与操作" : "只读查看" : "只读 · 写入未启用") + (_hasFreshData ? "" : " · 待刷新");
    public string OperationMessage { get => _operationMessage; private set => this.RaiseAndSetIfChanged(ref _operationMessage, value); }
    // 显示旧缓存与允许下发是两件事：所有当前可读字段取得本轮有效数据后才可考虑操作。
    // 字段草稿权限还要同时受页面状态、身份、写入开关、运行配置和连接代次约束。
    private void UpdateWriteState()
    {
        bool fresh = _selectionReady && _visibleFields.Any(f => f.CanRead) && _visibleFields.Where(f => f.CanRead).All(f => f.HasCurrentValue);
        if (_hasFreshData != fresh) { _hasFreshData = fresh; this.RaisePropertyChanged(nameof(WriteModeText)); }
        bool controls = CanUseControls;
        bool allowed = !_busy && controls;
        // 忙状态、权限、连接和新鲜度仍每轮复核，只有最终可操作结果变化才通知按钮绑定。
        if (_operateAllowed != allowed) { _operateAllowed = allowed; this.RaisePropertyChanged(nameof(CanOperate)); }
        if (_controlsAllowed != controls)
        {
            _controlsAllowed = controls;
            this.RaisePropertyChanged(nameof(CanUseControls));
            this.RaisePropertyChanged(nameof(WriteModeText));
        }
        foreach (var field in _fields) field.Edit.SetAllowed(allowed);
    }
    public IReadOnlyList<ComponentOption> Devices { get => _devices; private set => this.RaiseAndSetIfChanged(ref _devices, value); }
    public IReadOnlyList<ComponentOption> Components { get => _components; private set => this.RaiseAndSetIfChanged(ref _components, value); }
    public ComponentOption? SelectedDevice
    {
        get => _device;
        set { if (_device == value) return; this.RaiseAndSetIfChanged(ref _device, value); if (!_loading) { LoadComponents(); ResetPage(); QueueSelection(); } }
    }
    public ComponentOption? SelectedComponent
    {
        get => _component;
        set { if (_component == value) return; this.RaiseAndSetIfChanged(ref _component, value); if (!_loading) { ResetPage(); QueueSelection(); } }
    }
    public int SelectedTab { get => _tab; set { if (_tab == value) return; this.RaiseAndSetIfChanged(ref _tab, value); ResetPage(); if (!_loading) QueueSelection(); } }
    public int PositionPage { get => _page; set { value = Math.Clamp(value, 0, Math.Max(0, PositionPageCount - 1)); if (_page == value) return; this.RaiseAndSetIfChanged(ref _page, value); if (!_loading) QueueSelection(); } }
    public const int PositionPageSize = 10;
    public int PositionPageCount => Math.Max(1, (PositionIndices.Count + PositionPageSize - 1) / PositionPageSize);
    public string PositionPageText => $"第 {PositionPage + 1} / {PositionPageCount} 页 · 每页最多 {PositionPageSize} 个位置";
    protected virtual IReadOnlyList<int> PositionIndices => _fields.Select(f => ComponentBindingCatalog.PositionIndex(f.Binding)).Where(i => i >= 0).Distinct().Order().ToArray();
    protected IReadOnlyList<PlcDisplayField> Fields => _fields;
    public IReadOnlyList<PlcDisplayField> Header { get => _header; private set => this.RaiseAndSetIfChanged(ref _header, value); }
    public IReadOnlyList<PlcDisplayField> Feedback { get => _feedback; private set => this.RaiseAndSetIfChanged(ref _feedback, value); }
    public IReadOnlyList<PlcDisplayField> Details { get => _details; private set => this.RaiseAndSetIfChanged(ref _details, value); }
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public IReadOnlyList<PlcDisplayField> VisibleFields => _visibleFields;
    protected abstract bool IsHeader(PlcDisplayField field);
    protected virtual bool IsPersistentFeedback(PlcDisplayField field) => false;
    protected abstract IReadOnlyList<PlcDisplayField> BuildDetails();
    protected virtual void BuildRows() { }
    protected virtual object? CaptureRows() => null;
    protected virtual void RestoreRows(object? rows) { }
    private void ResetPage() { _page = 0; this.RaisePropertyChanged(nameof(PositionPage)); }
    private void LoadComponents()
    {
        _loading = true;
        Components = Catalog.Components(_category, SelectedDevice?.Id);
        SelectedComponent = Components.FirstOrDefault(c => c.Id == SelectedComponent?.Id) ?? Components.FirstOrDefault();
        _loading = false;
    }
    // 配置戳由版本和内容摘要组成；配置真正变化才清理字段/页面缓存。
    // 只有运行状态变化时重排当前需求，避免普通刷新反复创建行。
    private void CheckConfiguration()
    {
        var state = _runtime.Status.State;
        if (_stamp == Catalog.Stamp && _state == state) return;
        bool configurationChanged = _stamp != Catalog.Stamp;
        _stamp = Catalog.Stamp; _state = state;
        if (!configurationChanged) { QueueSelection(); return; }
        _fieldCache.Clear(); _pageCache.Clear(); _preparedFor = null;
        _loading = true;
        Devices = Catalog.Devices(_category);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == SelectedDevice?.Id) ?? Devices.FirstOrDefault();
        _loading = false;
        LoadComponents(); QueueSelection();
    }
    // 相同设备/构件/子页/分页直接恢复已创建的字段和行。
    // 首次进入才从配置目录构造；跨子页持续显示的反馈也纳入当前需求集合。
    private void Prepare()
    {
        var key = (SelectedDevice?.Id, SelectedComponent?.Id, SelectedTab, PositionPage);
        if (_pageCache.TryGetValue(key, out var cached))
        {
            _fields = cached.Fields; Header = cached.Header; Feedback = cached.Feedback;
            Details = cached.Details; _visibleFields = cached.Visible; RestoreRows(cached.Rows);
        }
        else
        {
            var component = (SelectedDevice?.Id, SelectedComponent?.Id);
            if (!_fieldCache.TryGetValue(component, out _fields!))
                _fieldCache[component] = _fields = Catalog.Fields(_category, component.Item1, component.Item2);
            Header = _fields.Where(IsHeader).ToArray();
            Feedback = _fields.Where(IsPersistentFeedback).ToArray();
            Details = BuildDetails().Where(f => !Header.Contains(f)).ToArray();
            // 页首和手动区在所有子页持续显示；只保留这些反馈及当前子页/分页的需求。
            _visibleFields = Header.Concat(Feedback).Concat(Details).Distinct().ToArray();
            BuildRows();
            _pageCache[key] = new(_fields, Header, Feedback, Details, _visibleFields, CaptureRows());
        }
        this.RaisePropertyChanged(nameof(PositionPageCount));
        this.RaisePropertyChanged(nameof(PositionPageText));
    }
    // 立即使当前操作上下文失效、释放按住请求，再把多次选择变更合并到 Loaded 优先级处理。
    // 先保留显示对象并标为缓存，使切页输入和布局有机会先完成。
    private Task QueueSelection()
    {
        ++_revision;
        ++_pressRevision; _heldDisplay = null;
        _selectionReady = false;
        foreach (var field in _visibleFields) field.MarkCached();
        _release = _operations.ChangeTargetAsync(null, null, PlcProtocol.OpcUa, false);
        UpdateWriteState();
        return _queuedSelection ??= _timer.Dispatcher.InvokeAsync(async () =>
        {
            _queuedSelection = null;
            await ObserveAsync();
        }, DispatcherPriority.Loaded).Task.Unwrap();
    }
    private async Task ObserveAsync()
    {
        try { await ApplySelectionAsync(); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    // 真实申请从这里执行：先释放旧按住命令，再更新操作目标和当前可见变量集合。
    // 每次 await 后复核代次；网络申请慢于用户切页时，旧回调只结束自己，不能恢复旧显示范围。
    public async Task ApplySelectionAsync()
    {
        long revision = ++_revision;
        _selectionReady = false;
        if (!_navigated || !_visible)
        {
            Message = "页面已隐藏，采集需求已释放";
            await _session.SetAsync([], false);
            return;
        }
        await _release;
        if (revision != _revision) return;
        var protocol = Catalog.Configuration?.Devices.FirstOrDefault(d => d.Id == SelectedDevice?.Id)?.Protocol ?? PlcProtocol.Unknown;
        await _operations.ChangeTargetAsync(SelectedDevice?.Id, SelectedComponent?.Id, protocol, true);
        if (revision != _revision) return;
        var selection = (_stamp, SelectedDevice?.Id, SelectedComponent?.Id, SelectedTab, PositionPage);
        if (_preparedFor != selection) { Prepare(); _preparedFor = selection; }
        bool active = _navigated && _visible && _runtime.Status.State == PlcRuntimeState.Running;
        var displayed = _visibleFields;
        Message = !_navigated || !_visible ? "页面已隐藏，采集需求已释放" :
            _runtime.Status.State != PlcRuntimeState.Running ? "采集未启动 · 不自动连接 PLC" :
            !displayed.Any(f => f.CanRead) ? "当前构件没有已启用的可读绑定" : "等待当前显示变量的采集结果";
        var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice?.Id);
        _readEpoch = active && device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
        var requestedAt = DateTimeOffset.UtcNow;
        foreach (var field in displayed)
            if (active) field.BeginRefresh(_readEpoch, _stamp ?? "", requestedAt);
            else field.MarkCached("采集未启动");
        try
        {
            await _session.SetAsync(displayed, active);
            if (revision != _revision) return;
            _selectionReady = active && displayed.Any(f => f.CanRead);
            UpdateWriteState();
            RefreshSnapshots();
        }
        catch (Exception ex)
        {
            if (revision == _revision)
            {
                foreach (var field in displayed) field.MarkCached("采集申请失败");
                Message = "页面采集申请失败：" + ex.Message;
            }
            throw;
        }
    }
    // 快照刷新只从页面会话取内存值，不在 UI 线程直接读 PLC。
    // 连接代次变化时先重排申请，旧连接的数据可以留作缓存展示，但不能继续开放参数提交。
    public void RefreshSnapshots()
    {
        if (!_navigated || !_visible) return;
        try
        {
            if (_runtime.Status.State != PlcRuntimeState.Running)
            {
                foreach (var f in _visibleFields) f.MarkCached("采集未启动");
                UpdateWriteState();
                return;
            }
            if (!_selectionReady) return;
            var device = _runtime.Status.Devices.FirstOrDefault(d => d.DeviceId == SelectedDevice?.Id);
            var epoch = device?.State == PlcDeviceState.Connected ? device.ConnectionEpoch : 0;
            if (epoch != _readEpoch) { QueueSelection(); return; }
            _snapshotLookup.Clear();
            var snapshots = _session.Snapshots();
            // 复用本页已有权限反馈，直接进入参数页也能识别 PLC 侧调试；模式页保持隐藏、不额外采集。
            if (SelectedDevice is { } selected) _modeControl?.ObserveParameterDebugFeedback(snapshots, selected.Id);
            foreach (var snapshot in snapshots) _snapshotLookup.Add(snapshot.Key, snapshot);
            foreach (var f in _visibleFields) f.Apply(_snapshotLookup.GetValueOrDefault(f.Key));
            UpdateWriteState();
            Message = $"仅当前构件、子页及位置分页 · {_session.Keys.Count} 个可读变量 · PLC 周期按配置 · 隐藏时停止页面采集";
        }
        catch (Exception ex)
        {
            foreach (var f in _visibleFields) f.MarkCached("快照不可用");
            UpdateWriteState();
            Message = "内存快照读取失败：" + ex.Message; Logs.LogError(ex);
        }
    }
    public async Task RefreshAsync()
    {
        if (!_navigated || !_visible || _runtime.Status.State != PlcRuntimeState.Running) return;
        long revision = _revision;
        try { await _session.RefreshAsync(); if (revision == _revision) RefreshSnapshots(); }
        catch (Exception ex) { if (revision == _revision) { foreach (var f in _visibleFields) f.MarkCached("刷新失败"); UpdateWriteState(); Message = "刷新失败：" + ex.Message; } Logs.LogError(ex); }
    }
    // 页面内串行操作入口：忙状态阻止并发提交；按钮外观使用 CanUseControls，避免随短暂忙状态反复闪烁。
    // 异常只更新提示；只有明确成功的返回结果才显示成功，未知写入结果不能当作已执行。
    private async Task RunOperationAsync(Func<Task<PlcWriteReport>> operation, string successMessage = "写入已接受，请根据PLC反馈判断动作状态。", PlcDisplayField? busyField = null)
    {
        if (!CanOperate) { OperationMessage = _busy ? "上一项操作正在处理，请稍候。" : "实机写入未启用、未连接或当前页面尚未就绪。"; return; }
        _busy = true;
        if (busyField is not null) busyField.IsBusy = true;
        UpdateWriteState();
        try
        {
            var report = await operation();
            OperationMessage = report.Success ? successMessage :
                string.Join("；", report.Results.Where(r => r.State != Company.Hardware.PLC.Models.PlcWriteState.Succeeded || r.Verified == false)
                    .Select(r => r.Key.LogicalId + "：" + r.Message).Distinct());
        }
        catch (OperationCanceledException) { OperationMessage = "页面已切换，未发送的操作已取消。"; }
        catch (Exception ex) { OperationMessage = ex.Message; Logs.LogError(ex); }
        finally
        {
            if (busyField is not null) busyField.IsBusy = false;
            _busy = false; UpdateWriteState();
        }
    }
    public virtual Task OperateAsync(string action, int? position = null)
    {
        if (action == "Move" && _visibleFields.Any(f => f.Edit.IsDirty))
        { OperationMessage = "请先下发或撤销参数修改，再运行位置。"; return Task.CompletedTask; }
        return RunOperationAsync(async () =>
        {
            var context = _operations.Capture(); var revision = _revision;
            var report = await _operations.ExecuteAsync(action, position);
            if (action == "Stop") ShowCommandAcknowledgement(report, context, revision, ".Request.Stop", true);
            return report;
        });
    }
    protected Task TogglePositionRunAsync(PlcDisplayField? feedback, int position) => ToggleCommandAsync(feedback, $".Positions[{position}].Run");
    // 取反依据当前有效反馈，不依据按钮颜色、默认 False 或上一次点击次数。
    // 发送前还要确认命令和反馈绑定到同一地址，且布尔取反/比例/偏移配置一致，避免读写的不是同一个位。
    protected Task ToggleCommandAsync(PlcDisplayField? feedback, string suffix) => RunOperationAsync(async () =>
    {
        if (feedback is null || !_visibleFields.Contains(feedback) || !feedback.CanRead || !feedback.HasCurrentValue || feedback.BooleanValue is null)
            throw new InvalidOperationException("运行位尚无有效读取结果，请等待刷新。");
        if (_visibleFields.Any(f => f.Edit.IsDirty)) throw new InvalidOperationException("请先下发或撤销参数修改，再操作运行位。");
        var command = Catalog.Configuration?.ActiveVariables.FirstOrDefault(v => v.DeviceId == feedback.Binding.DeviceId &&
            v.Protocol == feedback.Binding.Protocol && v.LogicalId == SelectedComponent?.Id + suffix);
        if (command is null || command.DataType != PlcDataType.Bool || !command.Access.HasFlag(PlcAccess.Write) ||
            feedback.Binding.DataType != PlcDataType.Bool || command.Address != feedback.Binding.Address ||
            command.InvertBoolean != feedback.Binding.InvertBoolean || (command.Scale ?? 1) != (feedback.Binding.Scale ?? 1) || (command.Offset ?? 0) != (feedback.Binding.Offset ?? 0))
            throw new InvalidOperationException("运行命令与状态绑定不一致，不能取反写入。");
        long revision = _revision;
        var context = _operations.Capture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requestedAt = DateTimeOffset.UtcNow;
        PlcVariableSnapshot snapshot;
        try { snapshot = (await _session.ReadOnceAsync(feedback.Key, timeout.Token)).Single(); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new InvalidOperationException("读取运行位超时，本次未发送。"); }
        if (revision != _revision || _operations.Capture() != context || !snapshot.IsValid ||
            snapshot.ConnectionEpoch != context.ConnectionEpoch || snapshot.ConfigurationVersion != context.ConfigurationStamp ||
            snapshot.LastAttemptAt is not { } at || at < requestedAt || snapshot.EngineeringValue is not bool current)
            throw new InvalidOperationException("运行位读取失效或连接已变化，本次未发送。");
        feedback.Apply(snapshot);
        var report = await _operations.SetCommandAsync(context, suffix, !current);
        // 服务端确认成功即更新当前按钮；之后由正常页面轮询的PLC实际值接管。
        if (report.Success && revision == _revision && _operations.LoggedIn && _operations.Capture() == context)
            feedback.ShowAcceptedBoolean(!current, context.ConnectionEpoch, context.ConfigurationStamp);
        return report;
    }, "运行位写入成功，后续读取将更新按钮状态。", feedback);
    // 写入成功只能临时改变当前页同一字段的显示；页面/权限/连接已变则忽略这次迟到的成功通知。
    // 该显示值不充当采样数据，后续更新的有效读取仍会覆盖它。
    private void ShowCommandAcknowledgement(PlcWriteReport report, PlcWriteContext context, long revision, string suffix, bool value)
    {
        if (revision != _revision || !_operations.LoggedIn || _operations.Capture() != context) return;
        var command = Catalog.Configuration?.ActiveVariables.FirstOrDefault(v => v.DeviceId == context.DeviceId && v.LogicalId == SelectedComponent?.Id + suffix);
        if (command is null || !report.Results.Any(r => r.Key.LogicalId == command.LogicalId && r.Key.DeviceId == command.DeviceId &&
            r.State == Company.Hardware.PLC.Models.PlcWriteState.Succeeded && r.Verified != false)) return;
        foreach (var field in _visibleFields.Where(f => f.CanRead && f.Binding.DeviceId == command.DeviceId && f.Binding.Protocol == command.Protocol &&
            f.Binding.DataType == PlcDataType.Bool && f.Binding.Address == command.Address && f.Binding.InvertBoolean == command.InvertBoolean &&
            (f.Binding.Scale ?? 1) == (command.Scale ?? 1) && (f.Binding.Offset ?? 0) == (command.Offset ?? 0)))
            field.ShowAcceptedBoolean(value, context.ConnectionEpoch, context.ConfigurationStamp);
    }
    public Task PressJogAsync(bool positive) => PressMomentaryAsync(positive ? "Positive" : "Negative");
    // 报警复位及正反点动的按住入口：保存本次按下的页面和连接上下文，等待明确的松开复位。
    // 操作会话负责相反方向复位、取消及实际下发；界面只维护当前按住状态的显示版本。
    public async Task PressMomentaryAsync(string action)
    {
        if (!CanOperate || _heldDisplay is not null) return;
        string suffix = action switch { "Positive" => ".Request.JogPositive", "Negative" => ".Request.JogNegative", "Reset" => ".Request.ResetFault", _ => throw new ArgumentException("未知按住操作。") };
        var press = ++_pressRevision;
        _busy = true; UpdateWriteState();
        try
        {
            var context = _operations.Capture(); var revision = _revision;
            _heldDisplay = (context, revision, suffix);
            var report = await _operations.PressMomentaryAsync(action);
            if (press == _pressRevision && _operations.HasJog)
            {
                ShowCommandAcknowledgement(report, context, revision, suffix, true);
                if (action != "Reset") ShowCommandAcknowledgement(report, context, revision, action == "Positive" ? ".Request.JogNegative" : ".Request.JogPositive", false);
            }
            OperationMessage = report.Success ? "按下请求已接受，松开写入 False。" : string.Join("；", report.Results.Select(r => r.Message).Distinct());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OperationMessage = ex.Message; Logs.LogError(ex); }
        finally { if (!_operations.HasJog) _heldDisplay = null; _busy = false; UpdateWriteState(); }
    }
    // 释放使用操作会话里记录的原按钮目标，不根据当前下拉框重新定位。
    // 先递增按住显示版本，防止迟到的置位完成通知把已松开的按钮再次显示成按下。
    public async Task ReleaseJogAsync()
    {
        ++_pressRevision;
        var held = _heldDisplay; _heldDisplay = null;
        try
        {
            var result = await _operations.ReleaseJogAsync();
            if (held is { } target) ShowCommandAcknowledgement(result, target.Context, target.Revision, target.Suffix, false);
            if (result.Results.Count > 0) OperationMessage = result.Success ? "松开复位请求已接受。" : "松开复位未确认，请核对设备状态。";
        }
        catch (Exception ex) { OperationMessage = "松开复位失败，请核对设备状态。"; Logs.LogError(ex); }
    }
    public void UndoEdits()
    { foreach (var field in _visibleFields) field.Edit.Undo(); RefreshSnapshots(); OperationMessage = "已撤销当前页修改。"; }
    public sealed record ParameterInputRequest(PlcDisplayField Field, PlcWriteContext Context, long PageRevision);
    // 数字键盘打开时封存字段、连接/身份上下文和页面代次。
    // 无法获得有效上下文时只返回空请求，视图保持只读，不打开可输入键盘。
    ParameterEntry? IParameterPage.BeginEntry(object candidate)
    {
        if (candidate is not PlcDisplayField field || BeginParameterInput(field) is not { } request) return null;
        string? label = this is AxisParameterViewModel axis
            ? axis.ManualParameters.FirstOrDefault(parameter => ReferenceEquals(parameter.Field, field))?.Name : null;
        return PlcParameterEntry.Create(field, label ?? field.Name, input => CommitParameterInputAsync(request, input));
    }

    public ParameterInputRequest? BeginParameterInput(PlcDisplayField field)
    {
        if (!CanOperate || !_visibleFields.Contains(field) || !field.Edit.CanEdit || !field.HasCurrentValue)
        { OperationMessage = _operations.CanEditParameters ? "当前参数不可写，请确认写入开关、连接及最新读取结果。" : ReadOnlyHint; return null; }
        return new(field, _operations.Capture(), _revision);
    }
    // 确认后再次校验来源，而不是信任弹窗打开时的一次检查。
    // 中途换页、切换用户、断线重连或绑定失效均会阻止这份旧请求提交；只写当前一个字段。
    public Task CommitParameterInputAsync(ParameterInputRequest request, string input) => RunOperationAsync(async () =>
    {
        var field = request.Field;
        if (request.PageRevision != _revision || !_visibleFields.Contains(field) || !field.CanRead ||
            !field.HasCurrentValue ||
            _operations.Capture() != request.Context || !field.Binding.Access.HasFlag(PlcAccess.Write) ||
            field.Binding.WriteMode != "参数暂存")
            throw new InvalidOperationException("页面、连接或参数来源已变化，请重新打开输入框。");
        // 弹窗确认只写当前字段；取消和输入过程不改变草稿或PLC值。
        PlcWriteValueConverter.ConvertValue(field.Binding, input);
        var report = await _operations.SaveAsync(request.Context, [new(field.Key, input)]);
        field.Edit.Result = string.Join("；", report.Results.Select(r => r.Message));
        await RefreshAsync();
        return report;
    }, "参数已写入并回读一致。");
    // 批量保存仅收集当前可见页的脏草稿，并记下每个草稿版本。
    // 写入并回读一致才接受相应版本；用户期间再次编辑的内容不能被旧完成通知清除。
    public async Task SaveParametersAsync()
    {
        await RunOperationAsync(async () =>
        {
            var context = _operations.Capture();
            var dirty = _visibleFields.Where(f => f.Edit.IsDirty).ToArray();
            if (dirty.Length == 0) throw new InvalidOperationException("当前页没有需要下发的修改。");
            if (dirty.Any(f => !f.CanRead || !f.HasCurrentValue || !f.Binding.Access.HasFlag(PlcAccess.Write) || f.Binding.WriteMode != "参数暂存" || f.Edit.DraftEpoch != context.ConnectionEpoch))
                throw new InvalidOperationException("参数来源已失效，请撤销修改并重新读取后编辑。");
            var revisions = dirty.ToDictionary(f => f.Key, f => f.Edit.Revision);
            var report = await _operations.SaveAsync(context, dirty.Select(f => new PlcVariableWrite(f.Key, f.Edit.Draft)).ToArray());
            foreach (var result in report.Results)
            {
                var field = dirty.First(f => f.Key == result.Key); field.Edit.Result = result.Message;
                if (result.State == Company.Hardware.PLC.Models.PlcWriteState.Succeeded && result.Verified == true) field.Edit.Accept(revisions[result.Key]);
            }
            await RefreshAsync();
            return report;
        });
    }
    // 可见性由视图传入，最小化、隐藏和离页都会重新核对需求。
    // 对象保持常驻并不代表继续采集；缓存显示可保留，但操作资格必须重建。
    public Task SetViewVisibleAsync(bool visible)
    {
        if (_visible == visible) return _queuedSelection ?? Task.CompletedTask;
        _visible = visible;
        if (_navigated && visible) CheckConfiguration();
        UpdateObservation();
        return QueueSelection();
    }
    public void OnNavigatedTo(NavigationContext context) { _navigated = true; CheckConfiguration(); UpdateObservation(); QueueSelection(); }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { _navigated = false; UpdateObservation(); QueueSelection(); }
}

/// <summary>
/// 轴页面专用投影：组织概览反馈、手动参数及绝对/相对位置表。
/// 动作执行、会话失效和采集生命周期沿用上面的业务基类，PLC 地址仍从配置绑定取得。
/// </summary>
public sealed class AxisParameterViewModel(ComponentBindingCatalog catalog, ComponentPageSession session, IPlcRuntime runtime, ComponentOperationSession operations, ModeControlViewModel? modeControl = null)
    : ComponentParameterViewModel("轴", catalog, session, runtime, operations, modeControl), IAxisParameterPage
{
    Task IAxisParameterPage.MoveAsync(object row) => row is AxisPositionRow position ? TogglePositionRunAsync(position) : Task.CompletedTask;
    public sealed record ManualAxisParameter(string Name, PlcDisplayField? Field, string Unit = "")
    {
        public string Label => string.IsNullOrWhiteSpace(Unit) ? Name : $"{Name}（{Unit}）";
    }
    // 后缀用于从已加载字段中定位参数；中文名称只影响页面标签，不改写 Excel 中的 PLC 地址。
    // 单位优先使用字段配置，缺失时再依据当前轴的位置单位生成速度/加速度显示后缀。
    private static readonly (string Suffix, string Name)[] ManualParameterDefinitions =
    [
        (".st_Parameter.i_Jog_Vel", "Jog 速度"),
        (".st_Parameter.i_Stop_Dec", "停止减速度"),
        (".st_Parameter.i_Jog_Acc", "点动加速度"),
        (".st_Parameter.i_Jog_Dec", "点动减速度")
    ];
    private IReadOnlyList<ManualAxisParameter> _manualParameters = ManualParameterDefinitions.Select(p => new ManualAxisParameter(p.Name, null)).ToArray();
    public IReadOnlyList<ManualAxisParameter> ManualParameters => _manualParameters;
    private bool IsManualParameter(PlcDisplayField field) => ManualParameterDefinitions.Any(p => field.LogicalId == SelectedComponent?.Id + p.Suffix);
    private PlcDisplayField? Field(string suffix) => Fields.FirstOrDefault(f => f.LogicalId == SelectedComponent?.Id + suffix);
    private PlcDisplayField? CommandState(string name) => Field(".st_Action." + name);
    public PlcDisplayField? EnableState => CommandState("b_Power_Execute");
    public PlcDisplayField? HomeState => CommandState("b_Home_On");
    public PlcDisplayField? ResetState => CommandState("b_Reset_Execute");
    public PlcDisplayField? StopState => CommandState("b_Stop_Execute");
    public PlcDisplayField? PositiveState => CommandState("b_Forward_On");
    public PlcDisplayField? NegativeState => CommandState("b_Backward_On");
    public PlcDisplayField? ActualPosition => Field(".ActualPosition");
    public PlcDisplayField? ActualVelocity => Field(".ActualVelocity");
    public PlcDisplayField? ActualTorque => Field(".ActualTorque");
    public PlcDisplayField? TargetPosition => Field(".TargetPosition");
    public PlcDisplayField? MotorState => Field(".Enabled");
    public PlcDisplayField? OriginState => Field(".Homed");
    public PlcDisplayField? ErrorCode => Field(".FaultCode");
    public PlcDisplayField? NegativeLimit => Field(".NegativeLimit");
    public PlcDisplayField? OriginSensor => Field(".OriginSensor");
    public PlcDisplayField? PositiveLimit => Field(".PositiveLimit");
    public PlcDisplayField? ProbeState => Field(".st_In.b_Touch");
    private void NotifyCommandStates()
    {
        var distanceUnit = ActualPosition?.Unit;
        var parameters = ManualParameterDefinitions.Select(p =>
        {
            var field = Field(p.Suffix);
            var unit = !string.IsNullOrWhiteSpace(field?.Unit) ? field.Unit : string.IsNullOrWhiteSpace(distanceUnit) ? "" :
                distanceUnit + (p.Suffix.EndsWith("i_Jog_Vel", StringComparison.Ordinal) ? "/s" : "/s²");
            return new ManualAxisParameter(p.Name, field, unit);
        }).ToArray();
        if (!_manualParameters.SequenceEqual(parameters))
        {
            _manualParameters = parameters;
            this.RaisePropertyChanged(nameof(ManualParameters));
        }
        foreach (var name in new[] { nameof(EnableState), nameof(HomeState), nameof(ResetState), nameof(StopState), nameof(PositiveState), nameof(NegativeState),
            nameof(ActualPosition), nameof(ActualVelocity), nameof(ActualTorque), nameof(TargetPosition), nameof(MotorState), nameof(OriginState), nameof(ErrorCode),
            nameof(NegativeLimit), nameof(OriginSensor), nameof(PositiveLimit), nameof(ProbeState) }) this.RaisePropertyChanged(name);
    }
    // 使能、回原点和停止采用“依据当前有效反馈取反”的页面语义。
    // 报警复位及正反点动走单独的按住/松开入口，不能误当成一次点击取反。
    public override Task OperateAsync(string action, int? position = null) => action switch
    {
        "Enable" => ToggleCommandAsync(EnableState, ".Request.Enable"),
        "Home" => ToggleCommandAsync(HomeState, ".Request.Home"),
        "Stop" => ToggleCommandAsync(StopState, ".Request.Stop"),
        _ => Task.CompletedTask
    };
    private IReadOnlyList<AxisPositionRow> _positions = [];
    // 两张位置表分别持有已准备的行，切换子页不替换隐藏表的 ItemsSource。
    private IReadOnlyList<AxisPositionRow> _absolutePositions = [], _relativePositions = [];
    public IReadOnlyList<AxisPositionRow> AbsolutePositions => _absolutePositions;
    public IReadOnlyList<AxisPositionRow> RelativePositions => _relativePositions;
    public IReadOnlyList<AxisPositionRow> Positions
    {
        get => _positions;
        private set
        {
            if (ReferenceEquals(_positions, value)) return;
            this.RaiseAndSetIfChanged(ref _positions, value);
            if (SelectedTab == 3 && !ReferenceEquals(_absolutePositions, value))
            {
                _absolutePositions = value;
                this.RaisePropertyChanged(nameof(AbsolutePositions));
            }
            else if (SelectedTab == 4 && !ReferenceEquals(_relativePositions, value))
            {
                _relativePositions = value;
                this.RaisePropertyChanged(nameof(RelativePositions));
            }
            foreach (var name in new[] { nameof(PositionUnit), nameof(SpeedUnit), nameof(AccelerationUnit), nameof(DecelerationUnit), nameof(PositionColumnTitle) }) this.RaisePropertyChanged(name);
        }
    }
    public string PositionColumnTitle => SelectedTab == 4 ? "相对位置" : "绝对位置";
    public string PositionUnit => Positions.FirstOrDefault()?.Position?.Unit is { Length: > 0 } unit ? unit : ActualPosition?.Unit ?? "";
    public string SpeedUnit => Positions.FirstOrDefault()?.Speed?.Unit is { Length: > 0 } unit ? unit : PositionUnit.Length > 0 ? PositionUnit + "/s" : "";
    public string AccelerationUnit => Positions.FirstOrDefault()?.Acceleration?.Unit is { Length: > 0 } unit ? unit : PositionUnit.Length > 0 ? PositionUnit + "/s²" : "";
    public string DecelerationUnit => Positions.FirstOrDefault()?.Deceleration?.Unit is { Length: > 0 } unit ? unit : AccelerationUnit;
    protected override bool IsHeader(PlcDisplayField f) => new[] { "ActualPosition", "ActualVelocity", "ActualTorque", "TargetPosition", "Enabled", "Homed", "Fault", "FaultCode" }
        .Any(name => f.LogicalId == SelectedComponent?.Id + "." + name);
    protected override bool IsPersistentFeedback(PlcDisplayField f) => IsManualParameter(f) || new[] { "NegativeLimit", "OriginSensor", "PositiveLimit", "st_In.b_Touch",
        "st_Action.b_Power_Execute", "st_Action.b_Home_On", "st_Action.b_Reset_Execute", "st_Action.b_Stop_Execute", "st_Action.b_Forward_On", "st_Action.b_Backward_On" }
        .Any(name => f.LogicalId == SelectedComponent?.Id + "." + name);
    private static int ArrayIndex(string id)
    {
        int end = id.LastIndexOf(']'), start = id.LastIndexOf('[');
        return end > start && start >= 0 && int.TryParse(id[(start + 1)..end], out int index) ? index : -1;
    }
    private static bool IsPositionField(PlcDisplayField f) => f.LogicalId.Contains(".Positions[", StringComparison.Ordinal) ||
        f.LogicalId.Contains(".RelativePositions[", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(f.LogicalId, @"\.(?:i_(?:Abs|Rel)_(?:Pos|Vel|Acc|Dec)|b_(?:Abs|Rel)_On|b_Abs_Done|i_Abs|i_Rel)\[");
    protected override IReadOnlyList<int> PositionIndices => Fields.Where(f => f.LogicalId.Contains(SelectedTab == 4 ? ".st_Parameter.i_Rel_Pos[" : ".st_Parameter.i_Abs_Pos[", StringComparison.Ordinal))
        .Select(f => ArrayIndex(f.LogicalId)).Where(i => i >= 0).Distinct().Order().ToArray();
    private string[] PositionSuffixes(int index, bool relative) => relative
        ? [$".st_Parameter.i_Rel_Pos[{index}]", $".st_Parameter.i_Rel_Vel[{index}]", $".st_Parameter.i_Rel_Acc[{index}]", $".st_Parameter.i_Rel_Dec[{index}]", $".st_Time.i_Rel[{index}]", $".st_Action.b_Rel_On[{index}]"]
        : [$".st_Parameter.i_Abs_Pos[{index}]", $".Positions[{index}].Speed", $".Positions[{index}].Acceleration", $".Positions[{index}].Deceleration", $".st_Time.i_Abs[{index}]", $".st_Action.b_Abs_On[{index}]", $".st_Out.b_Abs_Done[{index}]"];
    protected override IReadOnlyList<PlcDisplayField> BuildDetails()
    {
        if (SelectedTab is 3 or 4)
        {
            var ids = PositionIndices.Skip(PositionPage * PositionPageSize).Take(PositionPageSize)
                .SelectMany(i => PositionSuffixes(i, SelectedTab == 4)).Select(s => SelectedComponent?.Id + s).ToHashSet(StringComparer.Ordinal);
            return Fields.Where(f => ids.Contains(f.LogicalId)).ToArray();
        }
        return Fields.Where(f => !IsManualParameter(f) && !IsPositionField(f)).Where(f => SelectedTab switch
        {
            0 => ComponentBindingCatalog.Meta(f.Binding, "参数分类") is "状态" or "控制反馈" or "参数应用" or "PLC诊断",
            1 => ComponentBindingCatalog.Meta(f.Binding, "参数分类") is "运动参数" or "PLC参数",
            2 => ComponentBindingCatalog.Meta(f.Binding, "参数分类") == "回原点参数",
            _ => false
        }).ToArray();
    }
    protected override void BuildRows()
    {
        NotifyCommandStates();
        bool relative = SelectedTab == 4;
        Positions = SelectedTab is not (3 or 4) ? [] : PositionIndices.Skip(PositionPage * PositionPageSize).Take(PositionPageSize).Select(index =>
        {
            var suffixes = PositionSuffixes(index, relative);
            var position = Field(suffixes[0]);
            return new AxisPositionRow(index, position, Field(suffixes[1]), Field(suffixes[2]), Field(suffixes[3]), Field(suffixes[4]))
            {
                IsRelative = relative,
                ConfiguredName = position is null ? "" : ComponentBindingCatalog.Meta(position.Binding, "位置名称"),
                RunState = Field(suffixes[5]),
                // 当前XML仅定义绝对位置到位反馈，不借用它表示相对位置完成。
                DoneState = relative ? null : Field(suffixes[6])
            };
        }).ToArray();
    }
    public Task TogglePositionRunAsync(AxisPositionRow row) => row.IsRelative
        ? ToggleCommandAsync(row.RunState, $".RelativePositions[{row.Index}].Run")
        : TogglePositionRunAsync(row.RunState, row.Index);
    protected override object CaptureRows() => Positions;
    protected override void RestoreRows(object? rows) { Positions = (IReadOnlyList<AxisPositionRow>)rows!; NotifyCommandStates(); }
}

using Company.Application.Main.Models;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Application.Share.Login;

namespace Company.Application.Main.Services.PLC;

/// <summary>每个页面各持有一个实例；所有需求更新串行，刷新等待可被切换或离页取消。</summary>
[ExposedService(Lifetime.Transient)]
public sealed class ComponentPageSession(IPlcAcquisitionService acquisition, IPlcDataProvider data) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _refreshSync = new();
    // 页面实例使用唯一 owner，避免两个相同类型页面的采集需求相互覆盖。
    private readonly string _owner = "HMI.Component." + Guid.NewGuid().ToString("N");
    private IPlcAcquisitionScope? _scope;
    private IReadOnlyList<PlcVariableKey> _keys = [];
    private CancellationTokenSource? _refresh;
    private long _revision;
    public event EventHandler? ValuesChanged
    {
        add => data.ValuesChanged += value;
        remove => data.ValuesChanged -= value;
    }
    public IReadOnlyList<PlcVariableKey> Keys => _keys;
    /// <summary>
    /// 更新本页面可读字段的采集范围；新请求递增版本并取消正在等待的刷新，旧排队请求自动失效。
    /// </summary>
    public async Task SetAsync(IReadOnlyList<PlcDisplayField> fields, bool active)
    {
        long revision = Interlocked.Increment(ref _revision);
        lock (_refreshSync) _refresh?.Cancel();
        await _gate.WaitAsync();
        try
        {
            // 等待门闩期间若已有更新请求，本次选择不再打开或更新采集范围。
            if (revision != _revision) return;
            // 只为页面活动且允许读取的字段建立需求，同一变量键去重后才交给采集服务。
            var selected = active ? fields.Where(f => f.CanRead).DistinctBy(f => f.Key).ToArray() : [];
            // 参数页面携带模式、调试请求和急停反馈，离页一起释放；不新增后台采集或周期。
            if (active && selected.Any(f => f.Binding.Category is "轴" or "气缸"))
            {
                var devices = selected.Select(f => f.Binding.DeviceId).ToHashSet();
                var guards = data.GetCatalog().Where(b => b.Enabled && b.Access.HasFlag(Company.Application.Share.PLC.Configuration.PlcAccess.Read) && devices.Contains(b.DeviceId) &&
                    b.LogicalId is "UnitMode[0].Mode" or "UnitMode[0].Status" or "UnitMode[0].Action[6]" or "UnitMode[0].Signal[5]")
                    .Select(b => new PlcDisplayField(b, ""));
                selected = selected.Concat(guards).DistinctBy(f => f.Key).ToArray();
            }
            if (selected.Length == 0) { await ReleaseAsync(); return; }
            var keys = selected.Select(f => f.Key).ToArray();
            var request = new PlcAcquisitionRequest(_owner, selected.Select(f => f.Binding.AcquisitionGroup).Distinct().ToArray(), keys);
            // Scope 内部需要取得采集锁；即使接口返回 Task，也不能在界面线程进入这些同步锁。
            await Task.Run(async () =>
            {
                if (_scope is null) _scope = await acquisition.OpenPageScopeAsync(request).ConfigureAwait(false);
                else
                {
                    try { await _scope.UpdateAsync(request).ConfigureAwait(false); }
                    catch (ObjectDisposedException)
                    {
                        // 运行服务重建配置后，旧范围已失效；只恢复当前仍有效的页面请求。
                        await ReleaseAsync().ConfigureAwait(false);
                        if (revision != _revision) return;
                        _scope = await acquisition.OpenPageScopeAsync(request).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
            // 异步打开范围期间仍可能切页；旧请求不能把旧键重新公布给界面。
            _keys = revision == _revision ? keys : [];
        }
        catch { await ReleaseAsync(); throw; }
        finally { _gate.Release(); }
    }
    /// <summary>
    /// 先清空页面键并摘下当前范围，再异步释放其采集需求，防止读者继续使用旧页面键。
    /// </summary>
    private async Task ReleaseAsync()
    {
        _keys = [];
        var old = _scope; _scope = null;
        if (old is not null) await Task.Run(async () => await old.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }
    /// <summary>
    /// 读取当前键对应的缓存快照，不触发新通讯。
    /// </summary>
    public IReadOnlyList<PlcVariableSnapshot> Snapshots()
    {
        var snapshots = _keys.Count == 0 ? [] : data.GetSnapshots(_keys);
        DebugParameterAccess.Observe(snapshots);
        return snapshots;
    }
    /// <summary>
    /// 在后台线程请求单键真实读取，将调用方取消传递给采集服务。
    /// </summary>
    public Task<IReadOnlyList<PlcVariableSnapshot>> ReadOnceAsync(PlcVariableKey key, CancellationToken token)
        => Task.Run(() => acquisition.ReadOnceAsync([key], token), token);
    /// <summary>
    /// 为当前范围发起可取消的显式刷新；取消旧刷新后只跟踪新任务，并在释放门闩后等待通讯完成。
    /// </summary>
    public async Task RefreshAsync()
    {
        Task? pending = null; CancellationTokenSource? refresh = null;
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_scope is not null)
                {
                    lock (_refreshSync)
                    {
                        _refresh?.Cancel();
                        refresh = new CancellationTokenSource(); _refresh = refresh;
                    }
                    var scope = _scope;
                    pending = Task.Run(() => scope.RefreshAsync(refresh.Token));
                }
            }
            finally { _gate.Release(); }
            // 通讯等待放在临界区外，使切换页面能够取得门闩、撤销需求并取消旧刷新。
            if (pending is not null) await pending;
        }
        catch (OperationCanceledException) when (refresh?.IsCancellationRequested == true) { }
        finally
        {
            lock (_refreshSync)
            {
                // 只移除本次刷新拥有的取消源；较新的刷新不会被旧任务结束时清空。
                if (ReferenceEquals(_refresh, refresh)) _refresh = null;
                refresh?.Dispose();
            }
        }
    }
    /// <summary>
    /// 以离页更新复用串行取消与范围释放流程。
    /// </summary>
    public async ValueTask DisposeAsync() => await SetAsync([], false);
}

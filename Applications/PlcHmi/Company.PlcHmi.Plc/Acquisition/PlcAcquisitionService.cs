using Company.PlcHmi.Plc.Configuration;
using Company.PlcHmi.Plc.Data;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.PlcHmi.Plc.Acquisition;

/// <summary>单个有效读取需求；Generation 标识变量本次激活，Pending 表示至少一个范围等待新的读取尝试。</summary>
internal sealed record PlcDemand(PlcBoundPoint Point, long Generation, bool Pending);
/// <summary>一轮读取开始时的需求及范围版本快照，用于返回后过滤已释放或已更新的页面需求。</summary>
internal sealed record PlcDemandCapture(IReadOnlyList<PlcDemand> Demands,
    IReadOnlyDictionary<Guid, long> ScopeVersions);

/// <summary>合并后台常驻与页面临时需求；负责需求版本和采集结果接纳，不建立通讯连接或调用 UI。</summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcAcquisitionService))]
public sealed class PlcAcquisitionService(PlcDataStore store, PlcFrameDispatcher dispatcher, PlcBackgroundSelection? selection = null) : IPlcAcquisitionService
{
    private sealed class ScopeState(string ownerId, HashSet<PlcVariableKey> keys)
    {
        internal string OwnerId = ownerId;
        internal long Version = 1;
        internal HashSet<PlcVariableKey> Keys = keys;
        internal HashSet<PlcVariableKey> Pending = new(keys);
        internal TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    // 范围、活动变量代次和运行计划均由同一把锁保护；检查有效性与写入缓存必须处于同一临界区。
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ScopeState> _scopes = new();
    private readonly Dictionary<PlcVariableKey, long> _active = new();
    private long _generation;
    private PlcRuntimePlan? _plan;
    private bool _accepting;
    internal void Configure(PlcRuntimePlan plan)
    {
        lock (_sync)
        {
            _plan = plan; _accepting = true;
            selection?.Reset();
            RecomputeActivity();
        }
    }
    public Task<IPlcAcquisitionScope> OpenPageScopeAsync(PlcAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IPlcAcquisitionScope>(Open(request, false));
    }
    private PlcAcquisitionScope Open(PlcAcquisitionRequest request, bool allowBackground)
    {
        lock (_sync)
        {
            var keys = Validate(request, allowBackground);
            var id = Guid.NewGuid(); var state = new ScopeState(request.OwnerId, keys);
            if (keys.Count == 0) state.Ready.TrySetResult();
            _scopes.Add(id, state); RecomputeActivity();
            return new(this, id, state.OwnerId);
        }
    }
    /// <summary>通过临时范围等待每个指定变量至少尝试一次，再返回缓存快照；读取完成仍可能包含无效值。</summary>
    public async Task<IReadOnlyList<PlcVariableSnapshot>> ReadOnceAsync(IReadOnlyCollection<PlcVariableKey> variables,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(variables);
        var keys = variables.ToArray();
        await using var scope = Open(new("ReadOnce", [], keys), true);
        await WaitReady(scope.Id, cancellationToken).ConfigureAwait(false);
        return store.GetSnapshots(keys);
    }
    public IDisposable RegisterConsumer(IPlcSampleConsumer consumer, IReadOnlyCollection<string> groupIds)
        => dispatcher.Register(consumer, groupIds);
    internal long Version(Guid id) { lock (_sync) return GetScope(id).Version; }
    internal Task Update(Guid id, PlcAcquisitionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var scope = GetScope(id);
            if (request.OwnerId != scope.OwnerId) throw new ArgumentException("不能变更采集范围的所有者。");
            var keys = Validate(request, false);
            // 取消旧版本等待并重建待尝试集合；不会把旧范围完成信号误当成新页面内容已读取。
            scope.Ready.TrySetCanceled(); scope.Version++; scope.Keys = keys; scope.Pending = new(keys);
            scope.Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (keys.Count == 0) scope.Ready.TrySetResult();
            RecomputeActivity();
        }
        return Task.CompletedTask;
    }
    /// <summary>推进版本并要求现有变量重新尝试；取消令牌只控制本次等待，范围本身仍然保留。</summary>
    internal Task Refresh(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var scope = GetScope(id);
            scope.Ready.TrySetCanceled(); scope.Version++; scope.Pending = new(scope.Keys);
            scope.Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (scope.Keys.Count == 0) scope.Ready.TrySetResult();
            return scope.Ready.Task.WaitAsync(token);
        }
    }
    private Task WaitReady(Guid id, CancellationToken token)
    { lock (_sync) return GetScope(id).Ready.Task.WaitAsync(token); }
    internal void Release(Guid id)
    {
        lock (_sync)
        {
            if (!_scopes.Remove(id, out var scope)) return;
            scope.Ready.TrySetCanceled(); RecomputeActivity();
        }
    }
    private ScopeState GetScope(Guid id) => _accepting && _scopes.TryGetValue(id, out var scope)
        ? scope : throw new ObjectDisposedException(nameof(PlcAcquisitionScope));
    private HashSet<PlcVariableKey> Validate(PlcAcquisitionRequest request, bool allowBackground)
    {
        if (!_accepting || _plan is null) throw new InvalidOperationException("PLC 运行服务尚未启动。");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Groups); ArgumentNullException.ThrowIfNull(request.Variables);
        if (string.IsNullOrWhiteSpace(request.OwnerId)) throw new ArgumentException("采集范围需要所有者标识。");
        var groups = request.Groups.ToHashSet(StringComparer.Ordinal);
        if (groups.Any(id => !_plan.Configuration.AcquisitionGroups.Any(g => g.Id == id)))
            throw new ArgumentException("采集组不存在。");
        var keys = request.Variables.ToHashSet();
        foreach (var key in keys)
        {
            if (!_plan.Points.TryGetValue(key, out var point)) throw new ArgumentException("变量未启用或不可读。");
            if (!allowBackground && point.Group.Lifetime != PlcAcquisitionLifetime.Page)
                throw new ArgumentException("后台变量由常驻采集管理，页面通过缓存查看。");
            if (groups.Count != 0 && !groups.Contains(point.Group.Id)) throw new ArgumentException("变量不属于所选采集组。");
        }
        return keys;
    }
    private void RecomputeActivity()
    {
        // 活动集合是动态后台选择与所有页面范围的并集；只有重新加入的键才取得新代次，防止迟到结果复活旧需求。
        var keys = (_accepting && _plan is not null
            ? _plan.Points.Values.Where(p => p.Group.Lifetime == PlcAcquisitionLifetime.Background)
                .GroupBy(p => (p.Key.DeviceId, p.Group.Id)).SelectMany(g => selection?.Select(g.Key.DeviceId, g.Key.Id,
                    g.Select(p => p.Key).ToArray(), DateTimeOffset.UtcNow) ?? g.Select(p => p.Key))
                .Concat(_scopes.Values.SelectMany(s => s.Keys)) : []).ToHashSet();
        if (keys.SetEquals(_active.Keys)) return;
        foreach (var removed in _active.Keys.Where(k => !keys.Contains(k)).ToArray()) _active.Remove(removed);
        foreach (var key in keys) if (!_active.ContainsKey(key)) _active.Add(key, ++_generation);
        // Store 从不回调 acquisition；保持统一的 acquisition -> store 锁顺序。
        store.SetActivity(keys);
    }
    /// <summary>为单台设备生成独立读取需求快照；待尝试集合可让 OnEnter 组或手动刷新立即进入下一轮。</summary>
    internal PlcDemandCapture Capture(string deviceId)
    {
        lock (_sync)
        {
            if (!_accepting || _plan is null) return new([], new Dictionary<Guid, long>());
            RecomputeActivity();
            var pending = _scopes.Values.SelectMany(s => s.Pending).ToHashSet();
            return new(_active.Where(kv => kv.Key.DeviceId == deviceId)
                .Select(kv => new PlcDemand(_plan.Points[kv.Key], kv.Value, pending.Contains(kv.Key))).ToArray(),
                _scopes.ToDictionary(kv => kv.Key, kv => kv.Value.Version));
        }
    }
    // 验证和应用处于同一范围锁内，避免页面释放/重新进入与旧结果竞争。
    internal IReadOnlyList<PlcVariableSnapshot> Apply(PlcDemandCapture capture, IReadOnlyList<PlcDemand> attempted,
        IReadOnlyList<Company.Hardware.PLC.Models.PlcReadResult> results, long epoch)
    {
        lock (_sync)
        {
            if (!_accepting) return [];
            // 仅接纳仍处于相同激活代次的结果；范围 Version 还会单独保护更新和刷新后的完成信号。
            var valid = attempted.Select((d, i) => (Demand: d, Result: results[i]))
                .Where(x => _active.TryGetValue(x.Demand.Point.Key, out var generation) && generation == x.Demand.Generation).ToArray();
            var samples = store.Apply(valid.Select(x => (x.Demand.Point, x.Result)).ToArray(), epoch);
            var attemptedKeys = attempted.Select(d => d.Point.Key).ToHashSet();
            foreach (var (id, version) in capture.ScopeVersions)
                if (_scopes.TryGetValue(id, out var scope) && scope.Version == version)
                {
                    // Ready 表示指定键均完成过尝试，不能解释为全部质量良好；质量由调用方检查快照。
                    scope.Pending.ExceptWith(attemptedKeys);
                    if (scope.Pending.Count == 0) scope.Ready.TrySetResult();
                }
            return samples;
        }
    }
    /// <summary>重连后将相关变量重新标记为待尝试，使仅进入时读取的页面组也能取得新会话结果。</summary>
    internal void Reconnected(string deviceId)
    {
        lock (_sync)
            foreach (var scope in _scopes.Values)
                scope.Pending.UnionWith(scope.Keys.Where(k => k.DeviceId == deviceId));
    }
    internal void Stop()
    {
        lock (_sync)
        {
            _accepting = false;
            foreach (var scope in _scopes.Values) scope.Ready.TrySetCanceled();
            _scopes.Clear(); RecomputeActivity();
        }
    }
}

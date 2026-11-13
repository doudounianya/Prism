using Company.Application.PLC.Configuration;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Logger;
using System.Diagnostics;

namespace Company.Application.PLC.Data;

/// <summary>线程安全的最新变量快照缓存；只读查询不触发通讯，当前值失效时保留最近有效值供显式追溯。</summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcDataProvider))]
public sealed class PlcDataStore(PlcValueMapper mapper) : IPlcDataProvider
{
    private readonly object _sync = new();
    private PlcRuntimePlan? _plan;
    private readonly Dictionary<PlcVariableKey, PlcVariableSnapshot> _values = new();
    // 新鲜度使用单调时钟，不受系统时间调整影响；UTC/带偏移时间只作为对外观测时间显示。
    private readonly Dictionary<PlcVariableKey, long> _freshSince = new();
    private readonly HashSet<PlcVariableKey> _active = new();
    private int _notificationPending;
    public event EventHandler? ValuesChanged;

    internal void Configure(PlcRuntimePlan plan)
    {
        lock (_sync)
        {
            _plan = plan; _values.Clear(); _freshSince.Clear(); _active.Clear();
            foreach (var key in plan.Points.Keys)
                _values.Add(key, new() { Key = key, ConfigurationVersion = plan.Stamp });
        }
        Notify();
    }
    public IReadOnlyList<PlcVariableBinding> GetCatalog()
    { lock (_sync) return Array.AsReadOnly(_plan?.Configuration.Variables.ToArray() ?? []); }
    public IReadOnlyList<PlcAcquisitionGroupDefinition> GetGroups()
    { lock (_sync) return Array.AsReadOnly(_plan?.Configuration.AcquisitionGroups.ToArray() ?? []); }
    public IReadOnlyList<PlcVariableSnapshot> GetSnapshots(IReadOnlyCollection<PlcVariableKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_sync) return Array.AsReadOnly(keys.Select(k => _values.TryGetValue(k, out var value)
            ? value : throw new ArgumentException("变量未启用或不具有读取权限。")).ToArray());
    }
    /// <summary>活动需求变化时清空当前值并切换 Pending/Inactive；重新启用的变量须重新采集，不能直接复用上次当前值。</summary>
    internal void SetActivity(IReadOnlyCollection<PlcVariableKey> keys)
    {
        bool changed = false;
        lock (_sync)
        {
            var active = keys.ToHashSet();
            foreach (var key in _values.Keys.ToArray())
            {
                if (active.Contains(key) == _active.Contains(key)) continue;
                var value = _values[key];
                _values[key] = value with { State = active.Contains(key) ? PlcValueState.Pending : PlcValueState.Inactive,
                    EngineeringValue = null, RawValue = null, IsRealtime = false, IsInitialAfterReconnect = false };
                if (active.Contains(key)) _freshSince[key] = Stopwatch.GetTimestamp();
                else _freshSince.Remove(key);
                changed = true;
            }
            _active.Clear(); _active.UnionWith(active);
        }
        if (changed) Notify();
    }
    internal IReadOnlyList<PlcVariableSnapshot> Apply(IReadOnlyList<(PlcBoundPoint Point, Company.Hardware.PLC.Models.PlcReadResult Result)> results,
        long epoch)
    {
        var samples = new List<PlcVariableSnapshot>();
        lock (_sync)
        {
            foreach (var (point, result) in results)
            {
                if (!_active.Contains(point.Key) || _plan is null) continue;
                var sample = mapper.Map(point, result, _values[point.Key], epoch, _plan.Stamp);
                _values[point.Key] = sample; samples.Add(sample);
                if (sample.IsValid) _freshSince[point.Key] = Stopwatch.GetTimestamp();
            }
        }
        if (samples.Count > 0) Notify();
        return samples.AsReadOnly();
    }
    /// <summary>设备丢失时立即清空活动变量当前值及实时标志；LastValidValue 和其时间仍表示历史观测。</summary>
    internal void MarkDeviceLost(string deviceId, string reason)
    {
        lock (_sync)
        {
            foreach (var key in _active.Where(k => k.DeviceId == deviceId))
                _values[key] = _values[key] with { State = PlcValueState.Invalid, EngineeringValue = null,
                    RawValue = null, Error = reason, IsRealtime = false, IsInitialAfterReconnect = false };
        }
        Notify();
    }
    /// <summary>仅检查配置了失效阈值的周期组；超时后清空当前值，后台组另产生按设备和组去重的监控缺口。</summary>
    internal IReadOnlyList<PlcMonitoringGap> Expire()
    {
        var gaps = new List<PlcMonitoringGap>();
        bool changed = false;
        lock (_sync)
        {
            if (_plan is null) return gaps;
            foreach (var key in _active)
            {
                var point = _plan.Points[key]; var value = _values[key];
                if (point.Group.Mode != PlcAcquisitionMode.PeriodicBatch || point.Group.StaleAfterMs is not > 0 ||
                    value.State == PlcValueState.Stale || !_freshSince.TryGetValue(key, out var since) ||
                    Stopwatch.GetElapsedTime(since).TotalMilliseconds < point.Group.StaleAfterMs) continue;
                changed = true;
                _values[key] = value with { State = PlcValueState.Stale, EngineeringValue = null,
                    RawValue = null, IsRealtime = false, Error = "超过配置的数据失效时间。" };
                if (point.Group.Lifetime == PlcAcquisitionLifetime.Background)
                    gaps.Add(new(key.DeviceId, point.Group.Id, value.ConfigurationVersion, DateTimeOffset.UtcNow,
                        value.LastValidAt, "采集数据超时失效。"));
            }
        }
        if (changed) Notify();
        return gaps.DistinctBy(g => (g.DeviceId, g.GroupId)).ToArray();
    }
    private void Notify()
    {
        // UI 通知允许合并；业务采集帧通过独立的有界队列逐帧分发。
        if (Interlocked.Exchange(ref _notificationPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Interlocked.Exchange(ref _notificationPending, 0);
            foreach (EventHandler handler in ValuesChanged?.GetInvocationList() ?? [])
                try { handler(this, EventArgs.Empty); } catch (Exception ex) { Logs.LogError(ex); }
        });
    }
}

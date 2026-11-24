using Company.PlcHmi.Shared.PLC.Capacity;
using Company.PlcHmi.Shared.PLC.Capacity.Models;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Logger;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>
/// 维护产能六班次快照的只读缓存与变更通知，共享状态机完成结构核对和小时确认。
/// 显示缓存与历史保存独立；本服务不将缓存观察直接标为已入库记录。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IProductionCapacityService))]
public sealed class ProductionCapacityService(IPlcBindingConfigProvider configuration, IPlcAcquisitionService acquisition,
    CapacityBindingResolver resolver, CapacityAcquisitionState state) : IProductionCapacityService, IPlcSampleConsumer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ProductionCapacitySnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _gaps = new(StringComparer.Ordinal);
    private int _notificationPending;
    public event EventHandler? Changed;
    /// <summary>
    /// 校验当前绑定计划并读取缓存；未配置、等待采样或观察已过期时返回对应状态。
    /// </summary>
    public ProductionCapacitySnapshot GetSnapshot(string deviceId)
    {
        var plan = resolver.Resolve(configuration.Current, deviceId, out var error);
        if (plan is null) return new() { DeviceId = deviceId, State = ProductionCapacityState.NotConfigured, Message = error };
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(deviceId, out var snapshot) || snapshot.ConfigurationVersion != plan.Stamp)
                return new() { DeviceId = deviceId, ConfigurationVersion = plan.Stamp, State = ProductionCapacityState.Waiting, Message = "等待后台产能采样。" };
            // 新鲜度按读取时刻动态判断，避免没有新帧时界面仍把最后一次 Ready 永久当作实时有效。
            if (snapshot.ObservedAt is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromMilliseconds(plan.StaleAfterMs) &&
                snapshot.State is ProductionCapacityState.Ready or ProductionCapacityState.Checking)
                return snapshot with { State = ProductionCapacityState.Stale, Message = "产能采样已过期；以下仅为最后观测数据。" };
            return snapshot;
        }
    }
    /// <summary>
    /// 显式请求全部产能字段的一次真实读取，使用调用方取消与十秒超时，再返回当前缓存。
    /// </summary>
    public async Task<ProductionCapacitySnapshot> RefreshAsync(string deviceId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var plan = resolver.Resolve(configuration.Current, deviceId, out _);
        if (plan is null) return GetSnapshot(deviceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // 仅显式刷新调用此入口；采集帧回调只组装缓存，绝不在回调中再次读取。
        await acquisition.ReadOnceAsync(plan.Fields.Keys.Select(plan.Key).ToArray(), timeout.Token).ConfigureAwait(false);
        return GetSnapshot(deviceId);
    }
    /// <summary>
    /// 接受当前配置产能帧并交给共享状态机，提交缓存前再次核对配置及缺口时间。
    /// </summary>
    public Task OnFrameAsync(PlcAcquisitionFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (frame.GroupId != "BG.Production") return Task.CompletedTask;
        var plan = resolver.Resolve(configuration.Current, frame.DeviceId, out _);
        if (plan is null || frame.ConfigurationVersion != plan.Stamp) return Task.CompletedTask;
        var next = state.Observe(plan, frame).Snapshot;
        lock (_sync)
        {
            // 状态机计算期间配置可能重载；二次检查避免旧配置结果覆盖当前页面缓存。
            if (configuration.Current is not { } current || current.Version + ":" + current.FileSha256 != plan.Stamp)
                return Task.CompletedTask;
            _snapshots.TryGetValue(frame.DeviceId, out var previous);
            // 会话/运行代次由状态机检查；运行服务重新启动时连接计数可以从1重新开始。
            if (next.ConnectionEpoch != frame.ConnectionEpoch || previous?.ConfigurationVersion == plan.Stamp &&
                frame.CompletedAt <= previous.ObservedAt) return Task.CompletedTask;
            if (_gaps.TryGetValue(frame.DeviceId, out var gap) && frame.CompletedAt <= gap) return Task.CompletedTask;
            _snapshots[frame.DeviceId] = next;
        }
        Notify(); return Task.CompletedTask;
    }
    /// <summary>
    /// 将有效监控缺口传给状态机，同时保留最后显示数据并标记 Stale、清除结构身份。
    /// </summary>
    public Task OnGapAsync(PlcMonitoringGap gap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (gap.GroupId != "BG.Production") return Task.CompletedTask;
        state.Gap(gap);
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(gap.DeviceId, out var old) || old.ConfigurationVersion != gap.ConfigurationVersion ||
                gap.DetectedAt < old.ObservedAt || _gaps.GetValueOrDefault(gap.DeviceId) > gap.DetectedAt) return Task.CompletedTask;
            _gaps[gap.DeviceId] = gap.DetectedAt;
            _snapshots[gap.DeviceId] = old with { State = ProductionCapacityState.Stale, Identity = "", Message = "产能采集中断；仅保留最后观测数据：" + gap.Reason };
        }
        Notify(); return Task.CompletedTask;
    }
    /// <summary>
    /// 合并并在线程池发送变更通知；逐个隔离订阅者异常，采集回调不等待界面处理。
    /// </summary>
    private void Notify()
    {
        if (Interlocked.Exchange(ref _notificationPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => { Interlocked.Exchange(ref _notificationPending, 0);
            foreach (EventHandler h in Changed?.GetInvocationList() ?? []) try { h(this, EventArgs.Empty); } catch (Exception ex) { Logs.LogError(ex); } });
    }
}

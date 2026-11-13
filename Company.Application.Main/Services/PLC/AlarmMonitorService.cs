using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Monitoring;
using Company.Application.Share.PLC.Monitoring.Models;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Logger;
using System.Text.RegularExpressions;

namespace Company.Application.Main.Services.PLC;

/// <summary>
/// 维护本次运行中的报警观察状态，区分有效布尔值、未知区间和恢复后核对的边界。
/// 这里只提供实时投影，持久事件和有效时段由独立历史服务处理。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IAlarmMonitor))]
public sealed class AlarmMonitorService(IPlcBindingConfigProvider configuration) : IAlarmMonitor, IPlcSampleConsumer
{
    private readonly object _sync = new();
    private readonly Dictionary<PlcVariableKey, AlarmRuntimeSnapshot> _states = new();
    private string? _version;
    private int _notificationPending;
    public event EventHandler? Changed;

    /// <summary>
    /// 配置版本或摘要变化时重建报警目录，只接受约定逻辑编号、当前启用且可读的后台字段。
    /// </summary>
    private void EnsureCatalog()
    {
        var config = configuration.Current;
        var version = config is null ? null : config.Version + ":" + config.FileSha256;
        if (_version == version) return;
        // 新配置与旧采样不能共享状态基线；清空后等待新版本真实观察建立状态。
        _version = version; _states.Clear();
        if (config is null) return;
        foreach (var binding in config.ActiveVariables.Where(v => v.Category == "报警" && v.AcquisitionGroup == "BG.Alarm" && v.Access.HasFlag(PlcAccess.Read)))
        {
            var match = Regex.Match(binding.LogicalId, @"^Alarm\[(\d+)\]$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int index)) continue;
            var key = new PlcVariableKey(binding.Protocol, binding.DeviceId, binding.LogicalId);
            _states[key] = new() { Key = key, AlarmIndex = index, ConfigurationVersion = version! };
        }
    }

    /// <summary>
    /// 在锁内按设备和编号返回报警的只读快照数组，不触发 PLC 读取。
    /// </summary>
    public IReadOnlyList<AlarmRuntimeSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            EnsureCatalog();
            return Array.AsReadOnly(_states.Values.OrderBy(v => v.Key.DeviceId).ThenBy(v => v.AlarmIndex).ToArray());
        }
    }

    /// <summary>
    /// 处理同组且同配置的报警帧，过滤旧连接、倒序及缺口之前的采样，再更新观察状态。
    /// </summary>
    public Task OnFrameAsync(PlcAcquisitionFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureCatalog();
            if (frame.GroupId != "BG.Alarm" || frame.ConfigurationVersion != _version) return Task.CompletedTask;
            foreach (var sample in frame.Samples)
            {
                // 设备、配置、连接代次和时间边界一起校验，防止晚到旧帧把新状态或缺口改回有效。
                if (sample.Key.DeviceId != frame.DeviceId || sample.ConfigurationVersion != _version ||
                    sample.ConnectionEpoch != frame.ConnectionEpoch || !_states.TryGetValue(sample.Key, out var old) ||
                    sample.ConnectionEpoch < old.ConnectionEpoch ||
                    (old.LastSampleAt is { } lastSample && frame.CompletedAt < lastSample) ||
                    (old.LastGapAt is { } lastGap && frame.CompletedAt <= lastGap)) continue;
                // 失效采样清空当前值，但保留最后有效值及未知起点；不能把缺失通讯值当作正常状态。
                if (!sample.IsValid || sample.EngineeringValue is not bool active)
                {
                    _states[sample.Key] = old with { Active = null, State = sample.IsValid ? PlcValueState.Invalid : sample.State,
                        Quality = sample.Quality, Error = sample.Error ?? "未取得有效Bool状态。",
                        UnknownSince = old.UnknownSince ?? old.LastValidAt ?? frame.CompletedAt,
                        ConnectionEpoch = sample.ConnectionEpoch, LastSampleAt = frame.CompletedAt };
                    continue;
                }
                // 重连首值或从未知恢复只能建立/核对基线，无法证明中断期间的实际变化时刻。
                bool resumed = sample.IsInitialAfterReconnect || old.UnknownSince is not null || old.ConnectionEpoch != sample.ConnectionEpoch;
                var at = sample.LastValidAt ?? frame.CompletedAt;
                var next = old with { Active = active, LastKnownActive = active, State = PlcValueState.Valid,
                    Quality = sample.Quality, LastValidAt = at, LastSampleAt = frame.CompletedAt, UnknownSince = null, Error = null, ConnectionEpoch = sample.ConnectionEpoch };
                // 只有连续有效基线下的变化才称为升降沿；首次 true 与恢复后 false 分别记录发现/核对语义。
                if (active && old.LastKnownActive != true)
                    next = next with { StartObservedAt = at, EndObservedAt = null,
                        BoundarySource = resumed || old.LastKnownActive is null ? "连接/恢复后发现，实际开始时间未知" : "有效采样上升沿" };
                else if (active && resumed)
                    next = next with { BoundarySource = "重连后仍活动，延续本次运行记录" };
                else if (!active && old.LastKnownActive == true)
                    next = next with { EndObservedAt = at, BoundarySource = resumed ? "恢复后核对结束，实际解除时间未知" : "有效采样下降沿" };
                else if (!active && old.LastKnownActive is null)
                    next = next with { BoundarySource = "首次有效False，仅建立基线" };
                _states[sample.Key] = next;
            }
        }
        Notify(); return Task.CompletedTask;
    }

    /// <summary>
    /// 将当前设备中不晚于缺口的报警置为未知，保留历史基线供下一次有效观察核对。
    /// </summary>
    public Task OnGapAsync(PlcMonitoringGap gap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureCatalog();
            if (gap.GroupId != "BG.Alarm" || gap.ConfigurationVersion != _version) return Task.CompletedTask;
            foreach (var key in _states.Keys.Where(k => k.DeviceId == gap.DeviceId).ToArray())
            {
                var old = _states[key];
                // 晚到缺口不得覆盖更新采样；相交时从最早已知未知起点延续，不编造中断开始精度。
                if (old.LastSampleAt > gap.DetectedAt || old.LastGapAt > gap.DetectedAt) continue;
                _states[key] = old with { Active = null, State = PlcValueState.Invalid, Quality = PlcObservationQuality.Unknown,
                    UnknownSince = old.UnknownSince ?? old.LastValidAt ?? gap.LastValidAt ?? gap.DetectedAt,
                    LastGapAt = gap.DetectedAt, Error = gap.Reason };
            }
        }
        Notify(); return Task.CompletedTask;
    }

    /// <summary>
    /// 用原子标记合并待发通知，在线程池逐个通知订阅者；订阅者异常记录后继续通知其他订阅者。
    /// </summary>
    private void Notify()
    {
        if (Interlocked.Exchange(ref _notificationPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Interlocked.Exchange(ref _notificationPending, 0);
            foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
                try { handler(this, EventArgs.Empty); } catch (Exception ex) { Logs.LogError(ex); }
        });
    }
}

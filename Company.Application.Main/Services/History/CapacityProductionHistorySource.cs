using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Company.Application.Main.Services.History.Models;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.Application.Main.Services.History;

/// <summary>排队的结算批次只来自真实的两次读取，确认后不因当前小时切换或断线丢弃。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class CapacityProductionHistorySource(IPlcBindingConfigProvider config, CapacityBindingResolver resolver,
    CapacityAcquisitionState state) : IPlcSampleConsumer
{
    private readonly object _sync = new();
    // 队列仅驻留内存；必须等同步服务完成日志刷盘后才移除，停止时仍未落盘的项需要显式报告。
    private readonly Queue<CapacityProductionHistoryBatch> _pending = new();
    private readonly Dictionary<string, (string Stamp, long Epoch, DateTimeOffset At)> _last = new(StringComparer.Ordinal);
    public event EventHandler? BatchReady;
    internal int PendingCount { get { lock (_sync) return _pending.Count; } }
    public bool Available => config.Current?.Devices.Count(d => d.Enabled && d.Protocol == PlcProtocol.OpcUa) == 1 &&
        resolver.Resolve(config.Current, config.Current.Devices.Single(d => d.Enabled && d.Protocol == PlcProtocol.OpcUa).Id, out _) is not null;
    /// <summary>
    /// 只查看当前队首确认批次，不重新读取 PLC，也不提前移除。
    /// </summary>
    public Task<CapacityProductionHistoryBatch?> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult(_pending.TryPeek(out var batch) ? batch : null);
    }
    /// <summary>
    /// 仅允许确认同一对象的队首批次，防止并发或过时确认跳过尚未可靠保存的事实。
    /// </summary>
    internal void Acknowledge(CapacityProductionHistoryBatch batch)
    {
        lock (_sync)
        {
            if (!_pending.TryPeek(out var head) || !ReferenceEquals(head, batch)) throw new InvalidOperationException("产能结算批次确认顺序不匹配。");
            _pending.Dequeue();
        }
    }
    /// <summary>
    /// 将当前配置及连接代次的真实采集帧交给状态机，只将其已确认小时按基线时间分组入队。
    /// </summary>
    public Task OnFrameAsync(PlcAcquisitionFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (frame.GroupId != "BG.Production") return Task.CompletedTask;
        var configuration = config.Current; var plan = resolver.Resolve(configuration, frame.DeviceId, out _);
        if (plan is null || frame.ConfigurationVersion != plan.Stamp) return Task.CompletedTask;
        // 与展示服务共享状态机；同一帧重复观察返回同一结果，本来源再用帧时间去重避免重复排队。
        var observation = state.Observe(plan, frame);
        if (observation.Snapshot.ConnectionEpoch != frame.ConnectionEpoch || observation.Snapshot.ObservedAt != frame.CompletedAt) return Task.CompletedTask;
        bool added = false;
        lock (_sync)
        {
            if (_last.TryGetValue(frame.DeviceId, out var previous) && previous.Stamp == plan.Stamp &&
                frame.CompletedAt <= previous.At) return Task.CompletedTask;
            _last[frame.DeviceId] = (plan.Stamp, frame.ConnectionEpoch, frame.CompletedAt);
            if (observation.Confirmed.Count == 0) return Task.CompletedTask;
            var device = configuration!.Devices.Single(d => d.Id == frame.DeviceId);
            // 指纹只包含来源端点、时间编码及有序字段定义，作为历史来源身份的一部分；不混入本次计数值。
            var canonical = JsonSerializer.Serialize(new { device.OpcUaEndpoint, plan.TimeEncoding,
                Fields = plan.Fields.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Protocol, p.Value.DataType, Address = p.Value.Address.ToString() }) });
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            // 一批小时可能共享前次读取时间；保留两次真实读取的基线和确认时刻，供入库前复核。
            foreach (var group in observation.Confirmed.GroupBy(p => p.BaselineUtc))
            {
                _pending.Enqueue(new() { DeviceId = frame.DeviceId, BindingFingerprint = fingerprint,
                    ConfigurationVersion = plan.Stamp, ConnectionEpoch = frame.ConnectionEpoch,
                    BaselineUtc = group.Key, ConfirmedUtc = group.Max(p => p.ConfirmedUtc),
                    Periods = group.Select(p => new CapacityProductionHistoryPeriod(p.Shift.ProductionDate, p.Shift.Code,
                        p.Hour.StartUtc, p.Hour.EndUtc, p.Hour.Quantity)).ToArray() });
                added = true;
            }
        }
        if (added) BatchReady?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    /// <summary>
    /// 把产能采集中断通知状态机，取消未完成的相邻确认；已经排队的确认批次仍等待可靠落盘。
    /// </summary>
    public Task OnGapAsync(PlcMonitoringGap gap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (gap.GroupId == "BG.Production") state.Gap(gap);
        return Task.CompletedTask;
    }
}

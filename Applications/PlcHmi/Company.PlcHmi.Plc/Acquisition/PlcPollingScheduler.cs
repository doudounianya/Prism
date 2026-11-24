using Company.PlcHmi.Plc.Configuration;
using Company.PlcHmi.Plc.Connections;
using Company.PlcHmi.Plc.Data;
using Company.PlcHmi.Plc.Options;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Hardware.PLC.Models;
using Company.Logger;
using System.Diagnostics;

namespace Company.PlcHmi.Plc.Acquisition;

/// <summary>每设备一个轮询任务，按需求、连接代次及组周期决定读取；另设任务检查缓存过期。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcPollingScheduler(PlcAcquisitionService acquisition, PlcConnectionManager connections,
    PlcDataStore store, PlcFrameDispatcher dispatcher, PlcRuntimeOptions options, PlcBackgroundSelection? selection = null)
{
    private CancellationTokenSource? _stop;
    private Task[] _workers = [];
    private PlcRuntimePlan? _plan;
    internal bool HasFault => _workers.Any(w => w.IsFaulted);
    internal void Start(PlcRuntimePlan plan)
    {
        _plan = plan; _stop = new();
        connections.StatusChanged += ConnectionChanged;
        _workers = plan.Devices.Count == 0 ? [] : plan.Devices.Select(d => Task.Run(() => Poll(d.Id, _stop.Token)))
            .Append(Task.Run(() => CheckFreshness(_stop.Token))).ToArray();
    }
    /// <summary>连接边界立即使当前值失效并发送后台监控缺口；Connected 则要求相关范围取得新样本。</summary>
    private void ConnectionChanged(PlcDeviceRuntimeStatus status)
    {
        var plan = _plan;
        if (plan is null) return;
        if (status.State == PlcDeviceState.Connected) acquisition.Reconnected(status.DeviceId);
        else
        {
            store.MarkDeviceLost(status.DeviceId, status.Error ?? "PLC 尚未连接。");
            foreach (var group in plan.Points.Values.Where(p => p.Key.DeviceId == status.DeviceId &&
                p.Group.Lifetime == PlcAcquisitionLifetime.Background).Select(p => p.Group.Id).Distinct())
                dispatcher.Gap(new(status.DeviceId, group, plan.Stamp, status.ChangedAt, null,
                    status.Error ?? "PLC 连接边界，期间报警和产能数据不能推定为正常。"));
        }
    }
    private async Task Poll(string deviceId, CancellationToken token)
    {
        // 以单调时钟记录已提交的尝试时间；变量重新激活或会话代次变化时无须等待原周期。
        var attempts = new Dictionary<PlcVariableKey, (long Generation, long Epoch, long At)>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var capture = acquisition.Capture(deviceId);
                var status = connections.GetStatuses().FirstOrDefault(d => d.DeviceId == deviceId);
                if (status?.State == PlcDeviceState.Connected)
                {
                    // Pending 优先强制读取；OnEnter 无周期补读，PeriodicBatch 才按 IntervalMs 再次到期。
                    var duePoints = capture.Demands.Where(d => d.Pending ||
                        !attempts.TryGetValue(d.Point.Key, out var last) || last.Generation != d.Generation || last.Epoch != status.ConnectionEpoch ||
                        (d.Point.Group.Mode == PlcAcquisitionMode.PeriodicBatch &&
                         Stopwatch.GetElapsedTime(last.At).TotalMilliseconds >= d.Point.Group.IntervalMs))
                        .ToArray();
                    // 动态常驻组的少量身份保护字段与数值必须同轮读取，不能被各自的上次采样时间拆开。
                    var managedDue = duePoints.Where(d => selection?.Managed(d.Point.Group.Id) == true)
                        .Select(d => d.Point.Group.Id).ToHashSet(StringComparer.Ordinal);
                    var due = duePoints.Concat(capture.Demands.Where(d => managedDue.Contains(d.Point.Group.Id)))
                        .DistinctBy(d => d.Point.Key)
                        .OrderBy(d => d.Point.Group.Id == "BG.Alarm" ? 0 : d.Point.Group.Lifetime == PlcAcquisitionLifetime.Background ? 1 : 2).ToArray();
                    if (due.Length > 0)
                    {
                        IReadOnlyList<PlcVariableSnapshot> samples = [];
                        DateTimeOffset started = default, completed = default; long epoch = 0;
                        var committed = await connections.ReadAsync(deviceId, due.Select(d => d.Point.Point).ToArray(), (batch, connectionEpoch) =>
                        {
                            started = batch.StartedAt; completed = batch.CompletedAt; epoch = connectionEpoch;
                            // 返回项不足时补成明确的 InvalidResponse；保持结果和请求按索引对应，不能用缺项伪造成功。
                            var results = due.Select((d, i) => i < batch.Results.Count ? batch.Results[i] :
                                new PlcReadResult(d.Point.Point, null, null, false, PlcDataQuality.Unknown, null, null, null,
                                    completed, new(PlcErrorKind.InvalidResponse, "读取返回的项目数量不足。"))).ToArray();
                            samples = acquisition.Apply(capture, due, results, epoch);
                        }, token).ConfigureAwait(false);
                        // 只有连接管理器确认本轮仍属于当前会话才记录尝试时间并发布后台帧；断线边界前后的结果不会混合。
                        if (committed)
                        {
                            foreach (var d in due) attempts[d.Point.Key] = (d.Generation, epoch, Stopwatch.GetTimestamp());
                            foreach (var group in samples.GroupBy(s => _plan!.Points[s.Key].Group))
                                if (group.Key.Lifetime == PlcAcquisitionLifetime.Background)
                                    await dispatcher.PublishAsync(new(deviceId, group.Key.Id, _plan!.Stamp,
                                        epoch, started, completed, Array.AsReadOnly(group.ToArray()), group.Key.CheckpointIntervalMs), token).ConfigureAwait(false);
                        }
                    }
                }
                // 每台设备只执行一轮；耗时超周期直接跳过错过的时隙，不积压补跑。
                await Task.Delay(options.SchedulerResolutionMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Logs.LogError(ex); throw; }
    }
    private async Task CheckFreshness(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var gap in store.Expire()) dispatcher.Gap(gap);
                await Task.Delay(options.SchedulerResolutionMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    internal async Task StopAsync()
    {
        _stop?.Cancel(); connections.StatusChanged -= ConnectionChanged;
        await Task.WhenAll(_workers).ConfigureAwait(false);
        if (_plan is not null)
            foreach (var group in _plan.Points.Values.Where(p => p.Group.Lifetime == PlcAcquisitionLifetime.Background)
                .DistinctBy(p => (p.Key.DeviceId, p.Group.Id)))
                dispatcher.Gap(new(group.Key.DeviceId, group.Group.Id, _plan.Stamp, DateTimeOffset.UtcNow,
                    null, "软件采集停止；停止期间的数据不可推定。"));
        _workers = []; _stop?.Dispose(); _stop = null; _plan = null;
    }
}

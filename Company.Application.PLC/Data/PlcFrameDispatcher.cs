using Company.Application.PLC.Options;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Logger;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Company.Application.PLC.Data;

/// <summary>用单读者有界队列按序分发后台业务帧和监控缺口，与可合并的界面缓存通知分离。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcFrameDispatcher(PlcRuntimeOptions options)
{
    /// <summary>后台消费者及其关心的组集合；注册只决定接收范围，不直接新增轮询变量。</summary>
    private sealed record Consumer(Guid Id, IPlcSampleConsumer Target, HashSet<string> Groups)
    {
        private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);
        private readonly object _failureGate = new();
        // 状态随注册令牌释放；帧和缺口分别恢复，防止成功的补偿缺口掩盖持续失败的帧。
        private readonly Dictionary<(string DeviceId, string GroupId, bool IsFrame), ConsumerFailure> _failures = new();
        private bool _registered = true;

        public void ResetFailures(bool unregister = false)
        {
            lock (_failureGate)
            {
                _failures.Clear();
                if (unregister) _registered = false;
            }
        }

        public void Failed(string deviceId, string groupId, bool isFrame, Exception error, Exception? gapError)
        {
            lock (_failureGate)
            {
                if (!_registered) return;
                var key = (deviceId, groupId, isFrame);
                var now = Stopwatch.GetTimestamp();
                var first = false;
                if (!_failures.TryGetValue(key, out var failure))
                {
                    _failures[key] = failure = new();
                    first = true;
                }
                var changed = failure.ExceptionType != error.GetType() || failure.Message != error.Message;
                failure.ExceptionType = error.GetType(); failure.Message = error.Message;
                failure.TotalFailures++;
                // 记录被合并期间出现的补偿异常，确保它仍会出现在周期汇总或恢复日志里。
                if (gapError is not null) failure.LastGapError = gapError;
                if (!first && !changed && Stopwatch.GetElapsedTime(failure.LastLogTimestamp, now) < FailureLogInterval)
                {
                    failure.SuppressedFailures++;
                    return;
                }
                var context = $"PLC 数据消费者 {Target.GetType().FullName}（注册 {Id}，设备 {deviceId}，组 {groupId}，{(isFrame ? "帧" : "缺口")}）处理失败；"
                    + $"本轮累计失败 {failure.TotalFailures} 次，此前合并重复异常 {failure.SuppressedFailures} 次。";
                // 一次帧失败及其补偿缺口作为一个失败周期，只写一条错误并保留两条异常链。
                Logs.LogError(failure.LastGapError is null
                    ? new InvalidOperationException(context, error)
                    : new AggregateException(context + "本轮最近的补偿缺口异常一并记录。", error, failure.LastGapError));
                failure.LastLogTimestamp = now; failure.SuppressedFailures = 0;
            }
        }

        public void Recovered(string deviceId, string groupId, bool isFrame)
        {
            lock (_failureGate)
            {
                if (!_failures.Remove((deviceId, groupId, isFrame), out var failure)) return;
                var message = $"PLC 数据消费者 {Target.GetType().FullName}（注册 {Id}，设备 {deviceId}，组 {groupId}，{(isFrame ? "帧" : "缺口")}）处理恢复；"
                    + $"本轮累计失败 {failure.TotalFailures} 次，最近一次错误日志后合并重复异常 {failure.SuppressedFailures} 次。";
                if (failure.LastGapError is not null)
                    message += " 本轮最近的补偿缺口异常：" + failure.LastGapError;
                Logs.LogInfo(message);
            }
        }
    }
    private sealed class ConsumerFailure
    {
        public Type? ExceptionType;
        public string? Message;
        public long TotalFailures;
        public long SuppressedFailures;
        public long LastLogTimestamp;
        public Exception? LastGapError;
    }
    private readonly ConcurrentDictionary<Guid, Consumer> _consumers = new();
    private readonly ConcurrentDictionary<(string, string), PlcMonitoringGap> _pendingGaps = new();
    private Channel<object>? _channel;
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    /// <summary>可重复释放的消费者注册令牌；原子移除释放动作以避免重复注销。</summary>
    private sealed class Registration(Action dispose) : IDisposable
    { private Action? _dispose = dispose; public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
    internal IDisposable Register(IPlcSampleConsumer target, IReadOnlyCollection<string> groups)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(groups);
        var id = Guid.NewGuid(); _consumers[id] = new(id, target, groups.ToHashSet(StringComparer.Ordinal));
        return new Registration(() =>
        {
            if (_consumers.TryRemove(id, out var consumer)) consumer.ResetFailures(unregister: true);
        });
    }
    internal void Start()
    {
        _pendingGaps.Clear();
        foreach (var consumer in _consumers.Values) consumer.ResetFailures();
        _stop = new();
        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(options.FrameQueueCapacity)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        _channel = channel; var token = _stop.Token;
        _worker = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    foreach (var gapKey in _pendingGaps.Keys)
                        if (_pendingGaps.TryRemove(gapKey, out var gap)) await Deliver(gap, token).ConfigureAwait(false);
                    await Deliver(item, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }
    internal async Task PublishAsync(PlcAcquisitionFrame frame, CancellationToken token)
    {
        var channel = _channel;
        if (channel is null) return;
        // 队列满时对生产者施加等待，保留业务帧顺序；同时标记处理延迟，避免把积压期间推定为连续正常监控。
        if (!channel.Writer.TryWrite(frame))
        {
            Gap(new(frame.DeviceId, frame.GroupId, frame.ConfigurationVersion, DateTimeOffset.UtcNow, null,
                "业务采集分发队列积压，正在等待消费者；采集可能延迟。"));
            await channel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
        }
    }
    internal void Gap(PlcMonitoringGap gap)
    {
        var channel = _channel;
        if (channel is not null && !channel.Writer.TryWrite(gap))
            // 缺口不能因队列满而静默消失；同设备同组保留最新缺口，读者处理后续项前优先投递。
            _pendingGaps[(gap.DeviceId, gap.GroupId)] = gap;
    }
    /// <summary>按组逐个等待消费者完成；帧处理异常转为该消费者的缺口通知，防止错误样本形成连续业务记录。</summary>
    private async Task Deliver(object item, CancellationToken token)
    {
        var groupId = item is PlcAcquisitionFrame frame ? frame.GroupId : ((PlcMonitoringGap)item).GroupId;
        var deviceId = item is PlcAcquisitionFrame deviceFrame ? deviceFrame.DeviceId : ((PlcMonitoringGap)item).DeviceId;
        var isFrame = item is PlcAcquisitionFrame;
        foreach (var consumer in _consumers.Values.Where(c => c.Groups.Contains(groupId)))
        {
            try
            {
                // 任务线程隔离同步阻塞的消费者；停止时不把未完成回调当作成功。
                await Task.Run(() => item is PlcAcquisitionFrame sample
                    ? consumer.Target.OnFrameAsync(sample, token)
                    : consumer.Target.OnGapAsync((PlcMonitoringGap)item, token), token).WaitAsync(token).ConfigureAwait(false);
                consumer.Recovered(deviceId, groupId, isFrame);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Logs.LogError("PLC 数据消费者停止时仍有未完成回调；该消费者必须响应取消。"); throw; }
            catch (Exception ex)
            {
                Exception? fallbackError = null;
                if (item is PlcAcquisitionFrame failed)
                {
                    try
                    {
                        await Task.Run(() => consumer.Target.OnGapAsync(new(failed.DeviceId, failed.GroupId,
                            failed.ConfigurationVersion, DateTimeOffset.UtcNow, null, "业务消费者处理失败。"), token), token)
                            .WaitAsync(token).ConfigureAwait(false);
                        consumer.Recovered(deviceId, groupId, isFrame: false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        consumer.Failed(deviceId, groupId, isFrame, ex, null);
                        throw;
                    }
                    catch (Exception gapError) { fallbackError = gapError; }
                }
                consumer.Failed(deviceId, groupId, isFrame, ex, fallbackError);
            }
        }
    }
    internal async Task StopAsync()
    {
        _channel?.Writer.TryComplete();
        // 先完成写端并给队列有限排空时间；期限最多 1000 毫秒，超时再取消消费者并等待工作任务退出。
        try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(1000, options.ShutdownTimeoutMs / 2))).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Logs.LogError("PLC 分发队列在停止期限内未排空，仍有未完成数据；已标记监控中断。");
            _stop?.Cancel(); await _worker.ConfigureAwait(false);
        }
        _channel = null; _stop?.Dispose(); _stop = null; _pendingGaps.Clear();
    }
}

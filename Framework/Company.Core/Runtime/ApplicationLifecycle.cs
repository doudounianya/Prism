using Company.Core.Ioc;

namespace Company.Core.Runtime;

/// <summary>
/// 统一持有后台启动和退出任务。参与服务只登记自身步骤，不依赖关闭事件的订阅顺序。
/// 每个停止步骤独立尝试；失败保留待清理责任，下次退出只重试未完成步骤。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IApplicationLifecycle))]
public sealed class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ApplicationLifecycleStep> _steps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _startupCancellation = new();
    private Task? _start, _stop;
    private volatile ApplicationRunState _state;
    private bool _stopRequested;
    private bool _startupCleanupFailed;
    public ApplicationRunState State => _state;

    public void Register(ApplicationLifecycleStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
        ArgumentNullException.ThrowIfNull(step.Start);
        ArgumentNullException.ThrowIfNull(step.Stop);
        lock (_sync)
        {
            if (_start is not null || _stopRequested) throw new InvalidOperationException("启动后不能追加后台生命周期步骤。");
            if (!_steps.TryAdd(step.Name, step)) throw new InvalidOperationException("后台步骤重复注册：" + step.Name);
        }
    }

    public Task StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_stopRequested) throw new InvalidOperationException("应用正在退出，不能重新启动后台服务。");
            if (_startupCleanupFailed) throw new InvalidOperationException("上次启动的资源清理未完成，请先退出应用。");
            if (_state == ApplicationRunState.Running) return Task.CompletedTask;
            // 只有启动失败且所有已尝试步骤已清理后才允许重试；各服务仍负责自身幂等性。
            if (_start is null || _start.IsCompleted)
            {
                _stopped.Clear();
                _start = Task.Run(() => StartCoreAsync(progress));
            }
            return _start.WaitAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(IProgress<string>? progress)
    {
        var attempted = new List<ApplicationLifecycleStep>();
        try
        {
            lock (_sync)
            {
                _startupCancellation.Token.ThrowIfCancellationRequested();
                _state = ApplicationRunState.Starting;
            }
            foreach (var step in Steps().OrderBy(s => s.StartOrder))
            {
                _startupCancellation.Token.ThrowIfCancellationRequested();
                progress?.Report(step.Name);
                attempted.Add(step); // 包含启动到一半失败的步骤，它也有清理责任。
                await step.Start(_startupCancellation.Token).ConfigureAwait(false);
            }
            lock (_sync)
            {
                _startupCancellation.Token.ThrowIfCancellationRequested();
                _state = ApplicationRunState.Running;
            }
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            foreach (var step in attempted.AsEnumerable().Reverse())
                try { await step.Stop().ConfigureAwait(false); _stopped.Add(step.Name); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            _state = ApplicationRunState.Faulted;
            _startupCleanupFailed = errors.Count > 1;
            if (errors.Count > 1) throw new AggregateException("后台启动失败，部分清理也未完成。", errors);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stop;
        lock (_sync)
        {
            _stopRequested = true;
            if (_stop is null || _stop.IsFaulted)
                _state = ApplicationRunState.Stopping; // 立即阻止新操作，不能等到第一个清理步骤才封口。
            if (_stop is null || _stop.IsFaulted)
                _stop = Task.Run(StopCoreAsync);
            stop = _stop;
        }
        // 不持有协调器锁执行第三方取消回调；回调异常也不能取消应用自己的清理任务。
        try { _startupCancellation.Cancel(); }
        catch (AggregateException) { System.Diagnostics.Trace.TraceWarning("启动取消回调失败，仍继续后台退出。"); }
        return stop.WaitAsync(cancellationToken);
    }

    private ApplicationLifecycleStep[] Steps() { lock (_sync) return _steps.Values.ToArray(); }
    private async Task StopCoreAsync()
    {
        Task? start;
        lock (_sync) start = _start;
        if (start is not null) try { await start.ConfigureAwait(false); } catch { /* 仍继续所有退出步骤。 */ }
        var errors = new List<Exception>();
        foreach (var step in Steps().OrderBy(s => s.StopOrder))
        {
            if (_stopped.Contains(step.Name)) continue;
            try { await step.Stop().ConfigureAwait(false); _stopped.Add(step.Name); }
            catch (Exception exception) { errors.Add(new InvalidOperationException("后台退出步骤失败：" + step.Name, exception)); }
        }
        _state = errors.Count == 0 ? ApplicationRunState.Stopped : ApplicationRunState.Faulted;
        if (errors.Count > 0) throw new AggregateException("应用后台退出未完成。", errors);
    }
}

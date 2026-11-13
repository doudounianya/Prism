using Company.Application.PLC.Acquisition;
using Company.Application.PLC.Configuration;
using Company.Application.PLC.Connections;
using Company.Application.PLC.Data;
using Company.Application.PLC.Options;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Logger;

namespace Company.Application.PLC.Services;

/// <summary>PLC 应用运行总入口；按同一配置协调缓存、需求、分发、调度和设备连接的启动与清理。</summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcRuntime))]
public sealed class PlcRuntime(IPlcBindingConfigProvider configuration, PlcRuntimeConfigurationBuilder builder,
    PlcConnectionManager connections, PlcAcquisitionService acquisition, PlcDataStore store,
    PlcFrameDispatcher dispatcher, PlcPollingScheduler scheduler, PlcRuntimeOptions options) : IPlcRuntime
{
    // 串行化启动与停止，避免在旧资源清理期间装入新计划；StopAsync 另共享同一个清理任务。
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stopSync = new();
    private Task _stopTask = Task.CompletedTask;
    private int _disposed;
    private PlcRuntimeStatus _status = new(PlcRuntimeState.Stopped, null, [], []);
    /// <summary>组合生命周期状态和当前设备状态；若轮询任务异常结束，对外报告 Faulted。</summary>
    public PlcRuntimeStatus Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            return status with { Devices = connections.GetStatuses(),
                State = status.State == PlcRuntimeState.Running && scheduler.HasFault ? PlcRuntimeState.Faulted : status.State };
        }
    }
    public async Task<PlcRuntimeStartResult> StartAsync(string? configurationFilePath = null, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_status.State is PlcRuntimeState.Running or PlcRuntimeState.Idle) return new(!scheduler.HasFault, Status);
            _status = new(PlcRuntimeState.Starting, null, [], []);
            try
            {
                options.Validate();
                var loaded = await configuration.LoadAsync(configurationFilePath, cancellationToken).ConfigureAwait(false);
                if (!loaded.Success)
                {
                    _status = new(PlcRuntimeState.Faulted, null, [], loaded.Diagnostics);
                    return new(false, Status);
                }
                var plan = builder.Build(loaded.Configuration!);
                cancellationToken.ThrowIfCancellationRequested();
                // 先配置缓存与需求，再启动消费通道；之后安装调度边界观察，最后启动设备连接任务。
                store.Configure(plan); acquisition.Configure(plan); dispatcher.Start();
                // 先安装状态观察，再启动连接线程，避免丢失首次连接边界。
                scheduler.Start(plan); connections.Start(plan);
                _status = new(plan.Devices.Count == 0 ? PlcRuntimeState.Idle : PlcRuntimeState.Running,
                    plan.Stamp, [], loaded.Diagnostics);
                return new(true, Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await Cleanup().ConfigureAwait(false);
                _status = new(PlcRuntimeState.Stopped, null, [], []); throw;
            }
            catch (Exception)
            {
                await Cleanup().ConfigureAwait(false);
                _status = new(PlcRuntimeState.Faulted, null, [], [new(PlcDiagnosticSeverity.Error,
                    "PLC_RUNTIME_START", "", 0, "", null, "PLC 运行配置无效或启动失败，请检查启用协议、采集组及连接配置。")]);
                Logs.LogError("PLC 运行服务启动失败，未沿用上次配置。");
                return new(false, Status);
            }
        }
        finally { _lifecycle.Release(); }
    }
    public Task ReconnectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (Status.State != PlcRuntimeState.Running) throw new InvalidOperationException("PLC 运行服务未运行。");
        return connections.ReconnectAsync(deviceId, cancellationToken);
    }
    /// <summary>重复停止共享清理任务；调用方取消只结束自身等待，不取消服务负责的资源回收。</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopSync)
        {
            if (_stopTask.IsCompleted) _stopTask = Task.Run(StopCore);
            // 调用者取消只停止等待；资源清理仍由运行服务负责。
            return _stopTask.WaitAsync(cancellationToken);
        }
    }
    private async Task StopCore()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_status.State == PlcRuntimeState.Stopped) return;
            _status = _status with { State = PlcRuntimeState.Stopping };
            await Cleanup().ConfigureAwait(false);
            _status = _status with { State = PlcRuntimeState.Stopped };
        }
        finally { _lifecycle.Release(); }
    }
    /// <summary>先停止接纳需求，再停止轮询和连接，最后排空分发；各阶段独立尝试并汇总清理异常。</summary>
    private async Task Cleanup()
    {
        acquisition.Stop();
        // 即使某项停止失败，其他资源仍必须释放；失败不能伪装为成功。
        var errors = new List<Exception>();
        try { await scheduler.StopAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        try { await connections.StopAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        try { await dispatcher.StopAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("PLC 运行服务停止失败。", errors);
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await StopAsync().ConfigureAwait(false);
    }
}

using Company.Application.PLC.Options;
using Company.Application.Share.Events;
using Company.Application.Share.PLC.Runtime;
using Company.Core.Ioc;
using Company.Logger;
using Prism.Events;

namespace Company.Application.PLC.Lifecycle;

/// <summary>把应用关闭所有硬件事件桥接到 PLC 异步停止；自动初始化时订阅，释放时退订并再次确保停止。</summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class PlcLifecycleAdapter : IDisposable
{
    private readonly IPlcRuntime _runtime;
    private readonly PlcRuntimeOptions _options;
    private readonly CloseAllHardwareEvent _closeEvent;
    private readonly SubscriptionToken _subscription;
    public PlcLifecycleAdapter(IPlcRuntime runtime, PlcRuntimeOptions options, IEventAggregator events)
    {
        _runtime = runtime; _options = options;
        _closeEvent = events.GetEvent<CloseAllHardwareEvent>();
        _subscription = _closeEvent.Subscribe(Close);
    }
    private void Close()
    {
        try
        {
            // 同步事件回调只等待配置的毫秒期限；超过期限记录未完成，底层运行服务仍继续其资源清理任务。
            if (!_runtime.StopAsync().Wait(_options.ShutdownTimeoutMs))
                Logs.LogError("PLC 停止超过等待期限，仍有清理任务未完成。");
        }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    public void Dispose() { _closeEvent.Unsubscribe(_subscription); Close(); }
}

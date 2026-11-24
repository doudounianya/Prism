using Company.Core.Ioc;
using Company.Core.Runtime;
using Company.Platform.Shared.Events;
using Prism.Events;
using System.Diagnostics;

namespace Company.Desktop;

/// <summary>旧全局关闭事件的兼容入口。事件只触发统一协调器，不再并行拥有各设备的关闭责任。</summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class ApplicationShutdownBridge : IDisposable
{
    private readonly CloseAllHardwareEvent _event;
    private readonly SubscriptionToken _token;
    private readonly IApplicationLifecycle _lifecycle;
    public ApplicationShutdownBridge(IEventAggregator events, IApplicationLifecycle lifecycle)
    {
        _lifecycle=lifecycle;
        _event=events.GetEvent<CloseAllHardwareEvent>();
        _token=_event.Subscribe(Close);
    }
    private void Close()
    {
        try
        {
            if (!Task.Run(()=>_lifecycle.StopAsync()).Wait(TimeSpan.FromSeconds(30)))
                Trace.TraceWarning("后台退出仍在进行；不能视为资源已全部释放。");
        }
        catch (Exception exception) { Trace.TraceError("后台退出未完成："+exception.GetType().Name); }
    }
    public void Dispose() => _event.Unsubscribe(_token);
}

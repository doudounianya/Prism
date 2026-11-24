using Company.PlcHmi.Plc.Options;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.Core.Ioc;
using Company.Logger;
using Company.Core.Runtime;
using Company.PlcHmi.Plc.Writing;

namespace Company.PlcHmi.Plc.Lifecycle;

/// <summary>仅登记 PLC 的启动和退出顺序；统一协调器持有任务，不再单独订阅关闭事件。</summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class PlcLifecycleAdapter : IDisposable
{
    private readonly IPlcRuntime _runtime;
    private readonly PlcRuntimeOptions _options;
    public PlcLifecycleAdapter(IPlcRuntime runtime, PlcRuntimeOptions options, IApplicationLifecycle lifecycle,
        IPlcOperationLifecycle operations)
    {
        _runtime = runtime; _options = options;
        lifecycle.Register(new("正在准备操作请求入口...", 0, 0, _ => { operations.Open(); return Task.CompletedTask; }, operations.QuiesceAsync));
        lifecycle.Register(new("正在加载 PLC 运行配置...", 20, 20, async token =>
        {
            var result = await runtime.StartAsync(cancellationToken: token).ConfigureAwait(false);
            // 保留已有行为：配置/连接诊断显示为降级状态，仍允许进入只读主界面。
            foreach (var diagnostic in result.Status.Diagnostics)
                Logs.LogWarning($"PLC {diagnostic.Code}: {diagnostic.Message}");
        }, () => runtime.StopAsync()));
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
    public void Dispose() => Close(); // 容器释放仅作幂等兜底，正常退出由协调器完成。
}

using Company.Application.Share.PLC.Runtime.Models;
namespace Company.Application.Share.PLC.Runtime;

/// <summary>PLC 运行生命周期契约；协调配置生效、设备连接、调度和释放，页面不直接管理底层客户端。</summary>
public interface IPlcRuntime : IAsyncDisposable
{
    /// <summary>当前生命周期、配置标记、设备状态及配置诊断的只读快照。</summary>
    PlcRuntimeStatus Status { get; }
    /// <summary>加载并启动指定配置；成功表示运行计划已启动，具体设备连接仍须查看设备状态。</summary>
    Task<PlcRuntimeStartResult> StartAsync(string? configurationFilePath = null, CancellationToken cancellationToken = default);
    /// <summary>请求停止并等待资源清理；调用者取消只停止本次等待，服务仍继续清理。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
    /// <summary>请求已启用设备重建连接；任务完成表示请求已提交，须观察新会话状态确认恢复。</summary>
    Task ReconnectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
}

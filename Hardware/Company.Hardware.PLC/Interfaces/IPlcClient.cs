using Company.Hardware.PLC.Models;

namespace Company.Hardware.PLC.Interfaces;

/// <summary>一个设备的通讯客户端。不管理采集周期、业务权限或自动重试写入。</summary>
public interface IPlcClient : IAsyncDisposable
{
    string DeviceId { get; }
    PlcConnectionState ConnectionState { get; }
    /// <summary>在后台线程通知；事件处理错误不会中断通讯。</summary>
    event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<PlcBatchResult<PlcReadResult>> ReadBatchAsync(
        IReadOnlyList<PlcPoint> points, CancellationToken cancellationToken = default);
    Task<PlcWriteResult> WriteAsync(
        PlcWriteRequest request, CancellationToken cancellationToken = default);
    Task<PlcBatchResult<PlcWriteResult>> WriteBatchAsync(
        IReadOnlyList<PlcWriteRequest> requests, CancellationToken cancellationToken = default);
}

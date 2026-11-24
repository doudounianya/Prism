using Company.PlcHmi.Shared.PLC.Configuration;
namespace Company.PlcHmi.Shared.PLC.Runtime.Models;

/// <summary>运行服务生命周期；Idle 表示配置启动成功但没有启用设备，Running 不保证所有设备都已连接。</summary>
public enum PlcRuntimeState { Stopped, Starting, Running, Idle, Faulted, Stopping }
/// <summary>单设备连接状态；Connected 需实际健康读取成功，而非仅 SDK 连接调用返回。</summary>
public enum PlcDeviceState { Disconnected, Connecting, Connected, Faulted }
/// <summary>设备不可变状态；ConnectionEpoch 在成功建立并验证新会话时递增，ChangedAt 记录状态变更时间。</summary>
public sealed record PlcDeviceRuntimeStatus(string DeviceId, PlcDeviceState State,
    long ConnectionEpoch, DateTimeOffset ChangedAt, string? Error)
{
    /// <summary>最近一次确认收到有效健康响应或业务读取成功项的时间，不代表每个变量都有效。</summary>
    public DateTimeOffset? LastCommunicationAt { get; init; }
    /// <summary>累计进入 Connecting 状态的尝试次数，用于展示自动重连进展。</summary>
    public int ConnectionAttempts { get; init; }
}
/// <summary>运行级状态快照；ConfigurationVersion 为版本号与文件哈希形成的生效标记，Diagnostics 保留配置加载信息。</summary>
public sealed record PlcRuntimeStatus(PlcRuntimeState State, string? ConfigurationVersion,
    IReadOnlyList<PlcDeviceRuntimeStatus> Devices, IReadOnlyList<PlcConfigurationDiagnostic> Diagnostics);
/// <summary>启动请求结果；Success 表示计划进入可运行状态，连接是否完成需继续查看各设备状态。</summary>
public sealed record PlcRuntimeStartResult(bool Success, PlcRuntimeStatus Status);

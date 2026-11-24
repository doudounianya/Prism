using Company.Core.Ioc;
namespace Company.PlcHmi.Plc.Options;

/// <summary>PLC 运行级限制及开关；与 Excel 中各采集组的业务周期分离，所有容量和毫秒参数须为正值。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcRuntimeOptions
{
    // 已确认启用轴/气缸写入；可通过环境变量0切回只读，未知配置值也保持只读。
    // 不改变读取、报警保存及产能同步开关，实际提交仍须通过页面及绑定权限校验。
    public bool EnableWrites { get; init; } = Environment.GetEnvironmentVariable("CRT_HMI_PLC_WRITES") is null or "1";
    /// <summary>单次通讯操作超时，单位毫秒；由客户端工厂传给底层 OPC UA 实现。</summary>
    public int OperationTimeoutMs { get; init; } = 5000;
    // 独立的只读会话检测，不改变 Excel 中的页面、报警和产能采集周期。
    public int HealthCheckIntervalMs { get; init; } = 3000;
    /// <summary>单次底层请求的节点上限，由通讯客户端执行分批。</summary>
    public int MaxNodesPerRequest { get; init; } = 500;
    /// <summary>轮询与新鲜度检查的扫描等待间隔，单位毫秒；不是每个变量固定采样周期。</summary>
    public int SchedulerResolutionMs { get; init; } = 25;
    /// <summary>后台业务帧队列容量，单位为队列项目数；满时生产者等待消费者。</summary>
    public int FrameQueueCapacity { get; init; } = 128;
    /// <summary>关闭硬件桥接的等待期限，单位毫秒；分发器也以此计算有限排空等待。</summary>
    public int ShutdownTimeoutMs { get; init; } = 10000;
    internal void Validate()
    {
        if (OperationTimeoutMs <= 0 || HealthCheckIntervalMs <= 0 || MaxNodesPerRequest <= 0 || SchedulerResolutionMs <= 0 || FrameQueueCapacity <= 0 || ShutdownTimeoutMs <= 0)
            throw new ArgumentException("PLC 运行参数必须为正整数。");
    }
}

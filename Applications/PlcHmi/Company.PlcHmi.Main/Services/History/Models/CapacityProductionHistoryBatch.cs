namespace Company.PlcHmi.Main.Services.History.Models;

/// <summary>相邻软件观察一致的已结束小时；没有PLC原子版本承诺。</summary>
public sealed record CapacityProductionHistoryPeriod(DateTime ProductionDate, string ShiftCode,
    DateTime StartUtc, DateTime EndUtc, uint Quantity);

/// <summary>
/// 同一设备、配置及连接代次中产生的小时确认批次；基线先于确认，入库只接受基线之前已结束小时。
/// </summary>
public sealed record CapacityProductionHistoryBatch
{
    public string DeviceId { get; init; } = "";
    // 来源指纹描述端点及字段定义；配置版本描述本次加载配置，二者用途不同。
    public string BindingFingerprint { get; init; } = "";
    public string ConfigurationVersion { get; init; } = "";
    public long ConnectionEpoch { get; init; }
    // 前一次真实一致读取与本次确认读取的 UTC 时刻；不代表 PLC 提供了原子缓存版本。
    public DateTime BaselineUtc { get; init; }
    public DateTime ConfirmedUtc { get; init; }
    // 保留延后小时计数供校验与演进使用；本模型不把尚未确认小时自动加入 Periods。
    public int DeferredHours { get; init; }
    public IReadOnlyList<CapacityProductionHistoryPeriod> Periods { get; init; } = [];
}

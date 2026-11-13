namespace Company.Application.Share.PLC.History.Models;

/// <summary>单个生产历史时段；数量与生产日、班次、UTC 时段边界及修订号一同保存，供同步校验和幂等应用。</summary>
public sealed record ProductionHistoryPeriod
{
    /// <summary>来源声明的时段有效标志；同步流程还会检查时间范围和数量结构。</summary>
    public bool Valid {get;init;}
    /// <summary>该时段归属的生产日期；班次跨午夜时不能简单以 UTC 起点日期替代。</summary>
    public DateTime ProductionDate {get;init;}
    public string ShiftCode {get;init;} = "";
    public DateTime PeriodStartUtc {get;init;}
    public DateTime PeriodEndUtc {get;init;}
    /// <summary>来源时段修订号，用于识别同一小时记录的后续修订。</summary>
    public long Revision {get;init;}
    /// <summary>按来源约定顺序提供的无符号计数量；同步实现负责验证数量项与配置维度一致。</summary>
    public IReadOnlyList<uint> Quantities {get;init;} = [];
}
/// <summary>一次历史来源读取的完整载荷；VersionBefore/After、Updating 和 Complete 共同表达读取期间的一致性。</summary>
public sealed record ProductionHistorySnapshot
{
    public string DeviceId {get;init;} = "";
    /// <summary>历史来源标识，与 DeviceId、SourceEpoch 共同区分数据来源。</summary>
    public string SourceId {get;init;} = "";
    /// <summary>标识来源版本世代；PLC重启/程序变化导致不连续时不能盲目覆盖原小时记录。</summary>
    public string SourceEpoch {get;init;} = "";
    /// <summary>来源快照捕获时间，使用 UTC。</summary>
    public DateTime CapturedUtc {get;init;}
    /// <summary>读取历史数据之前的版本标记；须与 VersionAfter 结合判断读取期间是否变化。</summary>
    public long VersionBefore {get;init;}
    /// <summary>读取历史数据之后的版本标记；版本不一致时不能将载荷视为稳定快照。</summary>
    public long VersionAfter {get;init;}
    /// <summary>来源是否正在更新历史数据；为 true 时即使部分时段可读也不能宣称完整稳定。</summary>
    public bool Updating {get;init;}
    /// <summary>来源是否声明本次读取完整；用于拒绝缺项或未完成载荷。</summary>
    public bool Complete {get;init;}
    public IReadOnlyList<ProductionHistoryPeriod> Periods {get;init;} = [];
}

using Company.Database.PLC.Models;

namespace Company.Application.Share.PLC.Capacity.Models;

/// <summary>查询来源：PlcCache 为软件观测缓存，DatabaseHistory 为已保存的生产历史。</summary>
public enum ProductionCapacityDataSource { PlcCache, DatabaseHistory }
/// <summary>产能观测状态；仅 Ready 可供正常数量展示，等待、检查、无效和过期状态须保留原因。</summary>
public enum ProductionCapacityState { NotConfigured, Waiting, Checking, Ready, Invalid, Stale }
/// <summary>一个小时槽的 PLC 观测；StartUtc/EndUtc 为 UTC 时间边界，Quantity 为 PLC 原始无符号数量。</summary>
public sealed record ProductionCapacityHour(int Slot, DateTime StartUtc, DateTime EndUtc, uint Quantity);
/// <summary>一个班次槽的 PLC 总量与小时槽；PlcQuantity 为班次字段本身，不应直接假定等于各小时之和。</summary>
public sealed record ProductionCapacityShift(int Slot, string Code, DateTime ProductionDate, DateTime StartUtc,
    DateTime EndUtc, uint PlcQuantity, IReadOnlyList<ProductionCapacityHour> Hours);

/// <summary>软件观测快照；没有 PLC 版本标志，不作为可持久化的原子快照。</summary>
/// <remarks>ConfigurationVersion 与 ConnectionEpoch 标识数据边界；Identity 用于检查同一组字段读取期间的业务身份是否一致。</remarks>
public sealed record ProductionCapacitySnapshot
{
    public string DeviceId { get; init; } = "";
    public int SourceIndex { get; init; }
    public string SourceName { get; init; } = "提升机";
    public ProductionCapacityState State { get; init; }
    public string Message { get; init; } = "产能未配置";
    public string ConfigurationVersion { get; init; } = "";
    public long ConnectionEpoch { get; init; }
    /// <summary>软件取得观测结果的时间，尚无可用观测时为空。</summary>
    public DateTimeOffset? ObservedAt { get; init; }
    public string Identity { get; init; } = "";
    public IReadOnlyList<ProductionCapacityShift> Shifts { get; init; } = [];
    /// <summary>观测和口径警告；展示 Ready 数量时也应保留这些上下文。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsUsable => State == ProductionCapacityState.Ready;
}

/// <summary>统一展示的时段结果；空时间或数量表示无法确认，Included 标识是否纳入本次统计。</summary>
public sealed record ProductionCapacityPeriod(DateTime? StartUtc, DateTime? EndUtc, decimal? Quantity,
    string State, string Source, bool Included);
/// <summary>统一查询的班次结果；Quantity 为采用的数量，PlcQuantity 单独保留 PLC 班次数用于比较。</summary>
public sealed record ProductionCapacityShiftResult(string Code, DateTime StartUtc, DateTime EndUtc,
    decimal? Quantity, decimal? PlcQuantity, string Status, IReadOnlyList<ProductionCapacityPeriod> Periods);
/// <summary>日产能查询结果，保留查询条件、来源、UTC 查询时间、班次细分及警告。</summary>
public sealed record ProductionCapacityDayResult(ProductionDayQuery Query, ProductionCapacityDataSource DataSource,
    DateTime QueriedUtc, string Status, string SourceStatus, IReadOnlyList<ProductionCapacityShiftResult> Shifts,
    IReadOnlyList<string> Warnings);
/// <summary>产能趋势查询结果，按相同来源返回日序列并携带来源状态和警告。</summary>
public sealed record ProductionCapacityTrendResult(ProductionTrendQuery Query, ProductionCapacityDataSource DataSource,
    DateTime QueriedUtc, string Status, string SourceStatus, IReadOnlyList<ProductionTrendDay> Days,
    IReadOnlyList<string> Warnings);

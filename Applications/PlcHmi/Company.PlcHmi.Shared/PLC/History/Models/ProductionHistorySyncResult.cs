using Company.PlcHmi.Shared.Persistence.Models;
namespace Company.PlcHmi.Shared.PLC.History.Models;
/// <summary>Queued仅表示已持久化到本地日志，不表示已经写入数据库。</summary>
/// <remarks>Sequence 可关联本地日志序号；InvalidPeriods 为拒绝的时段数，Applied 仅在取得数据库应用结果后携带该结果。</remarks>
public sealed record ProductionHistorySyncResult(string State, long? Sequence = null,
    int InvalidPeriods = 0, string? Error = null, ProductionApplyResult? Applied = null);

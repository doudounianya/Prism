using Company.PlcHmi.Shared.Persistence.Models;
using Company.PlcHmi.Shared.PLC.History.Models;
namespace Company.PlcHmi.Main.Services.History.Models;

/// <summary>
/// 本地日志的版本化事实封装；日志组件负责写入流标识与连续序号。
/// Kind 由写入器分派到对应载荷，不同载荷字段保留为可空以兼容同一日志流。
/// </summary>
public sealed record PlcHistoryJournalEntry
{
    public int FormatVersion {get;init;} = 1;
    // 流标识跨重启保持，序号在同一流内递增；两者共同标识一次可幂等重放的提交。
    public string StreamId {get;init;} = "";
    public long Sequence {get;init;}
    public string Kind {get;init;} = "";
    // Alarm 为报警观察/缺口，Production 为通用小时快照，CapacityProduction 为相邻确认的提升机小时。
    public AlarmObservationBatch? Alarm {get;init;}
    public ProductionHistorySnapshot? Production {get;init;}
    public CapacityProductionHistoryBatch? CapacityProduction {get;init;}
}

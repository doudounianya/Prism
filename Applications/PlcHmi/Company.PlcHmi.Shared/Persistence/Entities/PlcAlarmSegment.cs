namespace Company.PlcHmi.Shared.Persistence.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcAlarmSegment
{
    public string SegmentId {get;set;} = "";
    public string EventId {get;set;} = "";
    public DateTime StartUtc {get;set;}
    public DateTime? EndUtc {get;set;}
    public DateTime LastValidUtc {get;set;}
    public string? CloseReason {get;set;}
}

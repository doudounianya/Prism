namespace Company.Database.PLC.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcProductionHour
{
    public long Id {get;set;}
    public string DeviceId {get;set;} = "";
    public DateTime ProductionDate {get;set;}
    public string ShiftCode {get;set;} = "";
    public DateTime PeriodStartUtc {get;set;}
    public DateTime PeriodEndUtc {get;set;}
    public int CounterIndex {get;set;}
    public ulong Quantity {get;set;}
    public string SourceId {get;set;} = "";
    public string SourceEpoch {get;set;} = "";
    public long SourceRevision {get;set;}
    public string ContentHash {get;set;} = "";
    public DateTime FirstSyncedUtc {get;set;}
    public DateTime LastSyncedUtc {get;set;}
}

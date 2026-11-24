namespace Company.PlcHmi.Shared.Persistence.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcProductionSyncState
{
    public string DeviceId {get;set;} = "";
    public string SourceId {get;set;} = "";
    public DateTime LastAttemptUtc {get;set;}
    public DateTime? LastSuccessUtc {get;set;}
    public DateTime? AvailableFromUtc {get;set;}
    public DateTime? AvailableToUtc {get;set;}
    public int Added {get;set;}
    public int Updated {get;set;}
    public int Unchanged {get;set;}
    public int Rejected {get;set;}
    public string Status {get;set;} = "";
    public string? Error {get;set;}
}

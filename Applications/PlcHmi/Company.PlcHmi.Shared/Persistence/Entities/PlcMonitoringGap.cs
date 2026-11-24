namespace Company.PlcHmi.Shared.Persistence.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcMonitoringGap
{
    public string GapId {get;set;} = "";
    public string DeviceId {get;set;} = "";
    public string AcquisitionGroup {get;set;} = "";
    public DateTime StartUtc {get;set;}
    public DateTime DetectedUtc {get;set;}
    public DateTime? RecoveredUtc {get;set;}
    public string Reason {get;set;} = "";
    public string ConfigurationVersion {get;set;} = "";
}

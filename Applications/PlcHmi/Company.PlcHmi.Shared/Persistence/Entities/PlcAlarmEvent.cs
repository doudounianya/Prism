namespace Company.PlcHmi.Shared.Persistence.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcAlarmEvent
{
    public string EventId {get;set;} = "";
    public string DeviceId {get;set;} = "";
    public string AlarmKey {get;set;} = "";
    public int AlarmIndex {get;set;}
    public string? AlarmCode {get;set;}
    public string? Message {get;set;}
    public string? Severity {get;set;}
    public string? Component {get;set;}
    public DateTime StartObservedUtc {get;set;}
    public DateTime? EndObservedUtc {get;set;}
    public string StartKind {get;set;} = "";
    public string? EndKind {get;set;}
    public bool HasGap {get;set;}
    public DateTime? LastValidUtc {get;set;}
    public string Status {get;set;} = "Open";
    public string ConfigurationVersion {get;set;} = "";
    public string BindingFingerprint {get;set;} = "";
    public string? AcknowledgedBy {get;set;}
    public DateTime? AcknowledgedUtc {get;set;}
    public DateTime CreatedUtc {get;set;}
    public DateTime UpdatedUtc {get;set;}
}

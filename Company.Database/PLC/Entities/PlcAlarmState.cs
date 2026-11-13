namespace Company.Database.PLC.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcAlarmState
{
    public string DeviceId {get;set;} = "";
    public string AlarmKey {get;set;} = "";
    public string? OpenEventId {get;set;}
    public bool? CurrentActive {get;set;}
    public bool? LastKnownActive {get;set;}
    public string State {get;set;} = "Pending";
    public DateTime? LastValidUtc {get;set;}
    public DateTime? LastSampleUtc {get;set;}
    public DateTime? LastGapUtc {get;set;}
    public string RunId {get;set;} = "";
    public long ConnectionEpoch {get;set;}
    public string BindingFingerprint {get;set;} = "";
    public DateTime UpdatedUtc {get;set;}
}

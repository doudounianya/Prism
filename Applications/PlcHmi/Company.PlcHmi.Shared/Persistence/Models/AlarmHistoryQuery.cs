namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed record AlarmHistoryQuery(DateTime FromUtc, DateTime ToUtc, string? DeviceId = null,
    string? AlarmKey = null, int Page = 1, int PageSize = 50, bool Overlap = true,
    string? Severity = null, string? Keyword = null)
{
    public void Validate()
    {
        if (FromUtc.Kind != DateTimeKind.Utc || ToUtc.Kind != DateTimeKind.Utc || ToUtc <= FromUtc)
            throw new ArgumentException("查询范围必须使用 UTC，且结束时间大于开始时间。");
        if (Page < 1 || PageSize is < 1 or > 500 || (long)(Page - 1) * PageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Page));
        if (DeviceId?.Length > 64 || AlarmKey?.Length > 128 || Severity?.Length > 64 || Keyword?.Length > 256)
            throw new ArgumentException("设备、报警标识、等级或关键词超出允许长度。");
    }
}

/// <summary>日志记录的是观察事实；数据库恢复后再与未结束记录核对。</summary>
public sealed record AlarmObservation
{
    public string DeviceId {get;init;} = "";
    public string AlarmKey {get;init;} = "";
    public int AlarmIndex {get;init;}
    public bool? Active {get;init;}
    public DateTime ObservedUtc {get;init;}
    public DateTime? LastValidUtc {get;init;}
    public long ConnectionEpoch {get;init;}
    public bool Initial {get;init;}
    public string BindingFingerprint {get;init;} = "";
    public string? AlarmCode {get;init;}
    public string? Message {get;init;}
    public string? Severity {get;init;}
    public string? Component {get;init;}
}
public sealed record AlarmObservationBatch
{
    public string DeviceId {get;init;} = "";
    public string AcquisitionGroup {get;init;} = "BG.Alarm";
    public string RunId {get;init;} = "";
    public string ConfigurationVersion {get;init;} = "";
    public DateTime ObservedUtc {get;init;}
    public bool Initial {get;init;}
    public bool IsGap {get;init;}
    public DateTime? GapStartUtc {get;init;}
    public string? GapReason {get;init;}
    public IReadOnlyList<string> MonitoredKeys {get;init;} = [];
    public IReadOnlyList<AlarmObservation> Observations {get;init;} = [];
}

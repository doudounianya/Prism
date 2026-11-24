namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed record AlarmAnalysisRow
{
    public string DeviceId {get;set;} = "";
    public string AlarmKey {get;set;} = "";
    public long ObservedEvents {get;set;}
    public long SampledRises {get;set;}
    public long DiscoveredEvents {get;set;}
    public long UncertainEvents {get;set;}
    public decimal ObservedDurationMs {get;set;}
    public string? Message {get;set;}
    public string? Severity {get;set;}
    public string? Component {get;set;}
    public int DefinitionVersions {get;set;}
}
public sealed record AlarmAnalysisResult(IReadOnlyList<AlarmAnalysisRow> Items, long MonitoringGaps,
    string DurationMeaning = "有效观测段按查询范围裁剪；缺口不计入有效时长，次数不代表PLC精确事件数。");
public sealed record ProductionApplyResult(bool AlreadyApplied, int Added, int Updated, int Unchanged,
    int Rejected, string? Error = null);

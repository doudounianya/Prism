/*
 * 文件职责与维护说明：
 * 历史报警和报警分析共用的输入条件。
 * 界面日期、时分秒按本机时区解析，再转换为 UTC 交给查询服务；非法日期、夏令时歧义和格式错误在发起查询前反馈。
 */
using Company.PlcHmi.Shared.Persistence.Models;
using ReactiveUI;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Company.PlcHmi.Main.Models;

public sealed class AlarmQueryFilter : ReactiveObject
{
    private DateTime? _fromDate=DateTime.Today.AddDays(-6),_toDate=DateTime.Today.AddDays(1);
    private string _fromTime="00:00:00",_toTime="00:00:00",_deviceId="",_alarmKey="",_severity="",_keyword="";
    private int _pageSize=50;
    public DateTime? FromDate {get=>_fromDate;set=>this.RaiseAndSetIfChanged(ref _fromDate,value);}
    public DateTime? ToDate {get=>_toDate;set=>this.RaiseAndSetIfChanged(ref _toDate,value);}
    public string FromTime {get=>_fromTime;set=>this.RaiseAndSetIfChanged(ref _fromTime,value);}
    public string ToTime {get=>_toTime;set=>this.RaiseAndSetIfChanged(ref _toTime,value);}
    public string DeviceId {get=>_deviceId;set=>this.RaiseAndSetIfChanged(ref _deviceId,value);}
    public string AlarmKey {get=>_alarmKey;set=>this.RaiseAndSetIfChanged(ref _alarmKey,value);}
    public string Severity {get=>_severity;set=>this.RaiseAndSetIfChanged(ref _severity,value);}
    public string Keyword {get=>_keyword;set=>this.RaiseAndSetIfChanged(ref _keyword,value);}
    public int PageSize {get=>_pageSize;set=>this.RaiseAndSetIfChanged(ref _pageSize,value);}
    public IReadOnlyList<int> PageSizes {get;}=[20,50,100];
    public IReadOnlyList<string> Severities {get;}=["","故障","警告","提示"];
    private static string? Optional(string value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    // 生成规范 UTC 查询前先校验本地日期与时间，拒绝夏令时跳变/重复区间。
    // 空筛选转换为 null 表示不限制，分页及起止边界再由查询模型统一验证。
    public bool TryCreate(int page,out AlarmHistoryQuery? query,out string error)
    {
        query=null;error="";
        try {
            DateTime Utc(DateTime? date,string time){
                if(date is null||!TimeSpan.TryParseExact(time?.Trim(),["hh\\:mm","hh\\:mm\\:ss"],CultureInfo.InvariantCulture,out var clock)||clock<TimeSpan.Zero||clock>=TimeSpan.FromDays(1))
                    throw new ArgumentException("请选择日期，并按 HH:mm 或 HH:mm:ss 输入时间。");
                var local=DateTime.SpecifyKind(date.Value.Date+clock,DateTimeKind.Unspecified);
                if(TimeZoneInfo.Local.IsInvalidTime(local)||TimeZoneInfo.Local.IsAmbiguousTime(local))throw new ArgumentException("所选本地时间处于夏令时跳变区间，请调整时间。");
                return TimeZoneInfo.ConvertTimeToUtc(local,TimeZoneInfo.Local);
            }
            if(Optional(AlarmKey) is{} key&&!Regex.IsMatch(key,@"^Alarm\[\d+\]$"))throw new ArgumentException("报警标识格式应为 Alarm[0]，留空表示全部。");
            query=new(Utc(FromDate,FromTime),Utc(ToDate,ToTime),Optional(DeviceId),Optional(AlarmKey),page,PageSize,true,Optional(Severity),Optional(Keyword));
            query.Validate();return true;
        }catch(ArgumentException ex){error=ex.Message;query=null;return false;}
    }
    public void Apply(AlarmHistoryQuery query)
    {
        var from=TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(query.FromUtc,DateTimeKind.Utc),TimeZoneInfo.Local);
        var to=TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(query.ToUtc,DateTimeKind.Utc),TimeZoneInfo.Local);
        FromDate=from.Date;FromTime=from.ToString("HH:mm:ss");ToDate=to.Date;ToTime=to.ToString("HH:mm:ss");
        DeviceId=query.DeviceId??"";AlarmKey=query.AlarmKey??"";Severity=query.Severity??"";Keyword=query.Keyword??"";PageSize=query.PageSize;
    }
}

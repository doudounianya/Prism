/*
 * 文件职责与维护说明：
 * 报警页面的纯显示转换：统一报警时间、持续时间、状态及边界文字。
 * 仅根据传入的历史/运行数据格式化，不补写事件、不推断未观测时段。
 */
using Company.Database.PLC.Entities;
using Company.Database.PLC.Models;
using System.Globalization;

namespace Company.Application.Main.Presentation;

public static class AlarmDisplayFormatter
{
    // MySQL DATETIME 读取时可能为 Unspecified；数据库字段约定为 UTC，不按本地时间再次解释。
    public static string Time(DateTime? utc)=>utc is{} at?
        DateTime.SpecifyKind(at,DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture):"—";
    public static string Duration(decimal? milliseconds)
    {
        if(milliseconds is null)return "—";
        decimal seconds=Math.Max(0,milliseconds.Value)/1000m;
        if(seconds<60)return seconds.ToString("0.###",CultureInfo.InvariantCulture)+" s";
        if(seconds<3600)return (seconds/60).ToString("0.##",CultureInfo.InvariantCulture)+" min";
        return (seconds/3600).ToString("0.##",CultureInfo.InvariantCulture)+" h";
    }
    public static string Start(string value)=>value switch{"ObservedRise"=>"采样上升沿","Discovered"=>"连接／恢复后发现，实际开始未知",_=>"来源未记录"};
    public static string End(string? value)=>value switch{"ObservedFall"=>"采样下降沿","Reconciled"=>"恢复后核对结束，实际解除时间未知",_=>"尚未记录结束"};
    public static string Status(PlcAlarmEvent e)=>e.Status switch{
        "BindingChanged"=>"绑定已变更，实际结束未知","BindingRemoved"=>"绑定已移除，实际结束未知",
        "Closed"=>"已记录结束",_=>"未记录结束，当前条件以实时反馈为准"};
    public static string Range(AlarmHistoryQuery q)=>$"{Time(q.FromUtc)} ≤ 时间 < {Time(q.ToUtc)}（本地时间）\n"+
        $"已查询：设备 {q.DeviceId??"全部"} · 报警 {q.AlarmKey??"全部"} · 等级 {q.Severity??"全部"} · 关键词 {q.Keyword??"无"}";
    public static string SegmentReason(string? reason)=>reason switch{
        null=>"片段未关闭，仅累计到最后有效观察","ObservedFall"=>"采样下降沿结束","ConnectionLost"=>"通讯中断",
        "ContinuityUnknown"=>"恢复前后连续性未知","BindingChanged"=>"变量绑定已变更","BindingRemoved"=>"变量绑定已移除",
        "InvalidObservation"=>"采样无效","RestartOrFirstObservation"=>"程序重启后重新核对","LocalJournalFailure"=>"本地保存故障",
        _=>"监控缺口："+reason};
    public static string Definition(PlcAlarmEvent e)=>$"报警：{e.AlarmCode??e.AlarmKey}  |  等级：{e.Severity??"未记录"}  |  构件：{e.Component??"未记录"}\n{e.Message??"未记录说明"}\n配置版本：{e.ConfigurationVersion}\n绑定指纹：{e.BindingFingerprint}";
}

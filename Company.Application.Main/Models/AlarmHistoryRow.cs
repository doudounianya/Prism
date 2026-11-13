/*
 * 文件职责与维护说明：
 * 历史报警查询的一行显示对象，负责把已持久化事件及观测边界转换为页面文字。
 * 没有结束时间或存在不确定区间时，应保留这些事实，不能把未观测到的状态解释成已恢复。
 */
using Company.Application.Main.Presentation;
using Company.Database.PLC.Entities;
using ReactiveUI;

namespace Company.Application.Main.Models;

public sealed class AlarmHistoryRow(PlcAlarmEvent alarmEvent,decimal? observedDurationMs) : ReactiveObject
{
    public PlcAlarmEvent Event { get; private set; } = alarmEvent with { };
    public decimal? ObservedDurationMs { get; private set; } = observedDurationMs;
    internal void Apply(AlarmHistoryRow next)
    {
        if (Event == next.Event && ObservedDurationMs == next.ObservedDurationMs) return;
        var before = DisplayValues();
        bool changedEvent = Event != next.Event, changedDuration = ObservedDurationMs != next.ObservedDurationMs;
        Event = next.Event with { }; ObservedDurationMs = next.ObservedDurationMs;
        if (changedEvent) this.RaisePropertyChanged(nameof(Event));
        if (changedDuration) this.RaisePropertyChanged(nameof(ObservedDurationMs));
        foreach (var entry in DisplayValues()) if (before[entry.Key] != entry.Value) this.RaisePropertyChanged(entry.Key);
    }
    private Dictionary<string, string> DisplayValues() => new()
    {
        [nameof(Device)] = Device, [nameof(Alarm)] = Alarm, [nameof(Severity)] = Severity,
        [nameof(Description)] = Description, [nameof(Component)] = Component, [nameof(StartText)] = StartText,
        [nameof(EndText)] = EndText, [nameof(ObservedDuration)] = ObservedDuration, [nameof(SpanText)] = SpanText,
        [nameof(Boundary)] = Boundary, [nameof(Status)] = Status, [nameof(Acknowledgement)] = Acknowledgement
    };
    public string Device=>Event.DeviceId;
    public string Alarm=>Event.AlarmKey;
    public string Severity=>Event.Severity??"未记录";
    public string Description=>Event.Message??"未记录说明";
    public string Component=>Event.Component??"—";
    public string StartText=>AlarmDisplayFormatter.Time(Event.StartObservedUtc);
    public string EndText=>AlarmDisplayFormatter.Time(Event.EndObservedUtc);
    public string ObservedDuration=>AlarmDisplayFormatter.Duration(ObservedDurationMs);
    public string SpanText=>AlarmDisplayFormatter.Duration(Math.Max(0m,((Event.EndObservedUtc??Event.LastValidUtc??Event.StartObservedUtc).Ticks-Event.StartObservedUtc.Ticks)/(decimal)TimeSpan.TicksPerMillisecond))+
        (Event.EndObservedUtc is null?"（截至最后观察）":"");
    public string Boundary=>AlarmDisplayFormatter.Start(Event.StartKind)+"；"+AlarmDisplayFormatter.End(Event.EndKind)+(Event.HasGap?"；包含监控缺口":"");
    public string Status=>AlarmDisplayFormatter.Status(Event);
    public string Acknowledgement=>Event.AcknowledgedUtc is{} at?$"{Event.AcknowledgedBy??"未记录人员"} / {AlarmDisplayFormatter.Time(at)}":"未记录确认";
}

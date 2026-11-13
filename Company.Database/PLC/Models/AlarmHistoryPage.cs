using Company.Database.PLC.Entities;
namespace Company.Database.PLC.Models;

public sealed record AlarmHistoryPage(long Total, IReadOnlyList<PlcAlarmEvent> Items)
{
    /// <summary>当前页每个事件的完整有效观测时长；不将未知间隔或现在时间补入。</summary>
    public IReadOnlyDictionary<string,decimal> ObservedDurations {get;init;} = new Dictionary<string,decimal>();
}
/// <summary>单写入工作器每次从仓储读取；失败后重新读取，不把未提交缓存当成已提交状态。</summary>
public sealed record AlarmHistoryAggregate(IReadOnlyList<PlcAlarmState> States,
    IReadOnlyList<PlcAlarmEvent> Events, IReadOnlyList<PlcAlarmSegment> Segments,
    IReadOnlyList<PlcMonitoringGap> Gaps);
public sealed record AlarmHistoryMutation(IReadOnlyList<PlcAlarmState> States,
    IReadOnlyList<PlcAlarmEvent> Events, IReadOnlyList<PlcAlarmSegment> Segments,
    IReadOnlyList<PlcMonitoringGap> Gaps);

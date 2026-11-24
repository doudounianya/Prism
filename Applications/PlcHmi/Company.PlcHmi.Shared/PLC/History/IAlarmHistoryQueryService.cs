using Company.PlcHmi.Shared.Persistence.Models;
namespace Company.PlcHmi.Shared.PLC.History;
/// <summary>持久化报警历史的只读查询与统计入口；与本次运行的实时报警监控快照分离。</summary>
public interface IAlarmHistoryQueryService
{
    /// <summary>按筛选及分页条件返回历史报警，取消令牌用于终止本次查询等待。</summary>
    Task<AlarmHistoryPage> QueryAsync(AlarmHistoryQuery query, CancellationToken token = default);
    /// <summary>按相同查询条件计算报警分析结果，统计口径由历史查询实现定义。</summary>
    Task<AlarmAnalysisResult> AnalyzeAsync(AlarmHistoryQuery query, CancellationToken token = default);
    /// <summary>按事件唯一标识取得详情；记录不存在时允许返回空。</summary>
    Task<AlarmHistoryDetail?> GetDetailAsync(string eventId, CancellationToken token = default);
}

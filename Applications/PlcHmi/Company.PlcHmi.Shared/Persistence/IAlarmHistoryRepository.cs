using Company.PlcHmi.Shared.Persistence.Models;
namespace Company.PlcHmi.Shared.Persistence;

public interface IAlarmHistoryRepository
{
    Task<long> GetCommittedSequenceAsync(string streamId, CancellationToken token);
    Task<AlarmHistoryAggregate> LoadAsync(string deviceId, string group, CancellationToken token);
    /// <summary>变更和提交位置必须在同一事务内；已经提交的序号不重复应用。</summary>
    Task ApplyAsync(string streamId, long sequence, AlarmHistoryMutation mutation, CancellationToken token);
    Task<AlarmHistoryPage> QueryAsync(AlarmHistoryQuery query, CancellationToken token);
    Task<AlarmAnalysisResult> AnalyzeAsync(AlarmHistoryQuery query, CancellationToken token);
    Task<AlarmHistoryDetail?> GetDetailAsync(string eventId, CancellationToken token);
}

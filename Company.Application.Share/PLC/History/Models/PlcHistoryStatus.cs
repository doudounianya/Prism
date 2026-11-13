namespace Company.Application.Share.PLC.History.Models;
/// <summary>历史流程状态；Pending 为待提交条目数，LastCommittedUtc 为最近成功提交的 UTC 时间，Production 为产能同步状态。</summary>
public sealed record PlcHistoryStatus(string State, long Pending, string? Error,
    DateTime? LastCommittedUtc, ProductionHistorySyncResult? Production = null)
{
    /// <summary>当前本地日志实际占用的字节数，不代表全部都是尚未入库的内容。</summary>
    public long JournalBytes { get; init; }
    /// <summary>本地日志配置容量上限，单位字节；零表示状态来源未提供该诊断项。</summary>
    public long JournalCapacityBytes { get; init; }
    /// <summary>允许等待数据库提交的最大条目数，与磁盘字节上限分别诊断。</summary>
    public long MaximumPending { get; init; }
    /// <summary>最近一次本地追加失败的原因；后续可靠追加成功后清除。</summary>
    public string? JournalError { get; init; }
    /// <summary>任一容量达到 80% 时提示；仅显示预警，不改变采样或删除待提交数据。</summary>
    public bool NearCapacity => (JournalCapacityBytes > 0 && JournalBytes >= JournalCapacityBytes * 0.8d) ||
        (MaximumPending > 0 && Pending >= MaximumPending * 0.8d);
}

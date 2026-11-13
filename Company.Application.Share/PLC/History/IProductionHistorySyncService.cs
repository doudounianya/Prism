using Company.Application.Share.PLC.History.Models;
namespace Company.Application.Share.PLC.History;
/// <summary>执行一次生产小时历史的读取、校验及持久化接纳流程，并返回分阶段状态。</summary>
public interface IProductionHistorySyncService
{
    /// <summary>触发单次同步；返回 Queued 只表示本地日志已接纳，数据库应用结果须查看后续状态。</summary>
    Task<ProductionHistorySyncResult> SyncOnceAsync(CancellationToken token = default);
}

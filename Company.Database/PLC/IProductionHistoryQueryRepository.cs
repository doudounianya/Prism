using Company.Database.PLC.Models;
namespace Company.Database.PLC;

/// <summary>仅查询已入库记录，与产能同步及事务写入接口隔离。</summary>
public interface IProductionHistoryQueryRepository
{
    Task<ProductionDayResult> QueryDayAsync(ProductionDayQuery query, ProductionDayWindow window, DateTime asOfUtc, CancellationToken token);
    Task<ProductionTrendResult> QueryTrendAsync(ProductionTrendQuery query, IReadOnlyList<ProductionDayWindow> windows, DateTime asOfUtc, CancellationToken token);
}


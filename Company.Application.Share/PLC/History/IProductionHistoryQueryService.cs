using Company.Database.PLC.Models;
namespace Company.Application.Share.PLC.History;

/// <summary>从持久化生产历史查询日明细及跨日趋势，不以当前 PLC 累计值倒推历史。</summary>
public interface IProductionHistoryQueryService
{
    /// <summary>按生产日查询条件返回日统计与细分时段，时间和设备范围由 query 给出。</summary>
    Task<ProductionDayResult> QueryDayAsync(ProductionDayQuery query, CancellationToken token = default);
    /// <summary>返回指定时间范围的生产趋势，并保留查询实现对缺口和有效性的判断。</summary>
    Task<ProductionTrendResult> QueryTrendAsync(ProductionTrendQuery query, CancellationToken token = default);
}


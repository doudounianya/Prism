using Company.Application.Share.PLC.Capacity.Models;
using Company.Database.PLC.Models;

namespace Company.Application.Share.PLC.Capacity;

/// <summary>按指定来源查询日产能与趋势；以同一结果契约支持 PLC 缓存观测和数据库历史。</summary>
public interface IProductionCapacityQueryService
{
    string DefaultDeviceId { get; }
    /// <summary>PLC 产能缓存发生变化时通知查询页面重新取数；不表示数据库历史查询结果已变更。</summary>
    event EventHandler? CacheChanged;
    Task<ProductionCapacityDayResult> QueryDayAsync(ProductionDayQuery query, ProductionCapacityDataSource source,
        CancellationToken token = default);
    Task<ProductionCapacityTrendResult> QueryTrendAsync(ProductionTrendQuery query, ProductionCapacityDataSource source,
        CancellationToken token = default);
}

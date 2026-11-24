using Company.PlcHmi.Shared.Persistence;
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.Persistence.Models;
using SqlSugar;
using Company.PlcHmi.Persistence.Journal;
namespace Company.PlcHmi.Persistence;

/// <summary>只执行查询，不调用事务写入入口、CodeFirst或同步服务。</summary>
public sealed class ProductionHistoryQueryRepository(HistoryTransaction sessions) : IProductionHistoryQueryRepository
{
    private static void ValidateWindow(ProductionDayWindow w, DateTime asOf)
    {
        if (asOf.Kind != DateTimeKind.Utc || w.StartUtc.Kind != DateTimeKind.Utc || w.NightStartUtc.Kind != DateTimeKind.Utc ||
            w.EndUtc.Kind != DateTimeKind.Utc || w.StartUtc >= w.NightStartUtc || w.NightStartUtc >= w.EndUtc)
            throw new ArgumentException("生产日UTC查询边界无效。");
    }
    private static async Task<(List<PlcProductionSyncState>, List<PlcMonitoringGap>)> Metadata(
        ISqlSugarClient db, string device, DateTime start, DateTime end, string? source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stateQuery = db.Queryable<PlcProductionSyncState>().AS("plc_production_sync_state").Where(s => s.DeviceId == device);
        stateQuery = ProductionSourcePolicy.IsCapacity(source) ? stateQuery.Where(s => s.SourceId == source) : stateQuery.Where(s => s.SourceId != ProductionSourcePolicy.CapacityDay);
        var states = await stateQuery.ToListAsync();
        token.ThrowIfCancellationRequested();
        var gapQuery = db.Queryable<PlcMonitoringGap>().AS("plc_monitoring_gap")
            .Where(g => g.DeviceId == device && g.AcquisitionGroup == "History.Production" &&
                g.StartUtc < end && (g.RecoveredUtc == null || g.RecoveredUtc > start));
        gapQuery = ProductionSourcePolicy.IsCapacity(source) ? gapQuery.Where(g => g.ConfigurationVersion.StartsWith(ProductionSourcePolicy.CapacityGapPrefix)) : gapQuery.Where(g => !g.ConfigurationVersion.StartsWith(ProductionSourcePolicy.CapacityGapPrefix));
        var gaps = await gapQuery.ToListAsync();
        token.ThrowIfCancellationRequested();
        return (states, gaps);
    }

    public async Task<ProductionDayResult> QueryDayAsync(ProductionDayQuery query, ProductionDayWindow window, DateTime asOfUtc, CancellationToken token)
    {
        query.Validate(); ValidateWindow(window, asOfUtc); token.ThrowIfCancellationRequested();
        if (window.ProductionDate != query.ProductionDate) throw new ArgumentException("生产日边界不匹配。");
        using var db = sessions.Open(); var counters = query.CounterIndices.ToArray();
        var rowQuery = db.Queryable<PlcProductionHour>().AS("plc_production_hour")
            .Where(r => r.DeviceId == query.DeviceId && r.ProductionDate == query.ProductionDate && counters.Contains(r.CounterIndex));
        rowQuery = ProductionSourcePolicy.IsCapacity(query.SourceId) ? rowQuery.Where(r => r.SourceId == query.SourceId) : rowQuery.Where(r => r.SourceId != ProductionSourcePolicy.CapacityDay);
        var rows = await rowQuery.OrderBy(r => r.PeriodStartUtc).OrderBy(r => r.CounterIndex).ToListAsync();
        var (states, gaps) = await Metadata(db, query.DeviceId, window.StartUtc, window.EndUtc, query.SourceId, token);
        return new(query, rows, states, gaps, asOfUtc);
    }

    public async Task<ProductionTrendResult> QueryTrendAsync(ProductionTrendQuery query, IReadOnlyList<ProductionDayWindow> windows, DateTime asOfUtc, CancellationToken token)
    {
        query.Validate(); token.ThrowIfCancellationRequested();
        if (windows.Count != (query.ToDate - query.FromDate).Days + 1) throw new ArgumentException("趋势日历不完整。");
        var parameters = new List<SugarParameter> {
            new("@device", query.DeviceId), new("@counter", query.CounterIndex), new("@asof", asOfUtc),
            new("@fromDate", query.FromDate), new("@toDate", query.ToDate), new("@capacitySource", ProductionSourcePolicy.CapacityDay) };
        var parts = new List<string>();
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i]; ValidateWindow(w, asOfUtc);
            if (w.ProductionDate != query.FromDate.AddDays(i)) throw new ArgumentException("趋势日历日期不连续。");
            parameters.AddRange([new("@d" + i, w.ProductionDate), new("@s" + i, w.StartUtc),
                new("@n" + i, w.NightStartUtc), new("@e" + i, w.EndUtc)]);
            parts.Add($"SELECT @d{i} ProductionDate,@s{i} DayStartUtc,@n{i} NightStartUtc,@e{i} DayEndUtc");
        }
        // UTC边界由应用按本地日历传入，不依赖MySQL时区表。先校验归班，再按日和班次聚合。
        string sourceFilter = ProductionSourcePolicy.IsCapacity(query.SourceId) ? "p.SourceId=@capacitySource" : "p.SourceId<>@capacitySource";
        string sql = "WITH windows AS (" + string.Join(" UNION ALL ", parts) + @"),
classified AS (
 SELECT p.*,CASE
 WHEN p.PeriodEndUtc<=p.PeriodStartUtc OR TIMESTAMPDIFF(MICROSECOND,p.PeriodStartUtc,p.PeriodEndUtc)>3600000000
   OR p.PeriodEndUtc>@asof THEN -1
 WHEN p.PeriodStartUtc>=w.DayStartUtc AND p.PeriodEndUtc<=w.NightStartUtc AND UPPER(TRIM(p.ShiftCode)) IN ('D','DAY','白班') THEN 0
 WHEN p.PeriodStartUtc>=w.NightStartUtc AND p.PeriodEndUtc<=w.DayEndUtc AND UPPER(TRIM(p.ShiftCode)) IN ('N','NIGHT','夜班') THEN 1
 ELSE -1 END Shift
 FROM plc_production_hour p JOIN windows w ON p.ProductionDate=w.ProductionDate
 WHERE " + sourceFilter + @" AND p.DeviceId=@device AND p.CounterIndex=@counter AND p.ProductionDate>=@fromDate AND p.ProductionDate<=@toDate
), ordered AS (
 SELECT c.*,MAX(PeriodEndUtc) OVER(PARTITION BY ProductionDate,Shift ORDER BY PeriodStartUtc,Id
 ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) PreviousEnd FROM classified c
)
SELECT ProductionDate,Shift,COUNT(*) Records,
 SUM(CASE WHEN Shift>=0 THEN CAST(Quantity AS DECIMAL(28,0)) ELSE 0 END) Quantity,
 SUM(CASE WHEN PreviousEnd>PeriodStartUtc THEN 1 ELSE 0 END) Overlaps,
 SUM(GREATEST(0,TIMESTAMPDIFF(MICROSECOND,PeriodStartUtc,PeriodEndUtc))/1000) CoverageMs,
 MIN(PeriodStartUtc) FirstUtc,MAX(PeriodEndUtc) LastUtc
FROM ordered GROUP BY ProductionDate,Shift ORDER BY ProductionDate,Shift";
        using var db = sessions.Open();
        var rows = await db.Ado.SqlQueryAsync<ProductionTrendAggregate>(sql, parameters.ToArray());
        token.ThrowIfCancellationRequested();
        var (states, gaps) = await Metadata(db, query.DeviceId, windows[0].StartUtc, windows[^1].EndUtc, query.SourceId, token);
        return new(query, rows, states, gaps, asOfUtc);
    }
}


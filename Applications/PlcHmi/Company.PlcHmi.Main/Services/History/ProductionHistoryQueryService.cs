using Company.PlcHmi.Shared.PLC.History;
using Company.Core.Ioc;
using Company.PlcHmi.Shared.Persistence;
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.Persistence.Models;
using Company.PlcHmi.Main.Services.PLC;
namespace Company.PlcHmi.Main.Services.History;

/// <summary>
/// 读取已保存产能并按对应来源的生产日历判定合计和完整性。
/// 缺失、重叠、尚未结束或包含监控缺口的班次不会被标为记录完整。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IProductionHistoryQueryService))]
public sealed class ProductionHistoryQueryService(IProductionHistoryQueryRepository repository) : IProductionHistoryQueryService
{
    /// <summary>
    /// 查询指定生产日，分别为白夜班及各计数器计算可确认合计、覆盖完整性与待核对警告。
    /// </summary>
    public async Task<ProductionDayResult> QueryDayAsync(ProductionDayQuery query, CancellationToken token = default)
    {
        query.Validate(); token.ThrowIfCancellationRequested();
        // 复制调用方集合，使异步查询使用固定的计数器选择，避免等待期间外部更改其内容。
        query = query with { CounterIndices = query.CounterIndices.ToArray() };
        var w = ProductionCapacityCalendar.HistoryWindow(query.ProductionDate, query.SourceId); var now = DateTime.UtcNow;
        var result = await repository.QueryDayAsync(query, w, now, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        int invalid = result.Records.Count(r => ProductionCalendar.Classify(r, w, now) < 0);
        if (invalid > 0) warnings.Add($"{invalid}条记录的日期、班次或时间不一致，未计入正常合计；请查看待核对明细。");
        var shifts = new List<ProductionShiftResult>();
        for (int shift = 0; shift < 2; shift++)
        {
            var start = shift == 0 ? w.StartUtc : w.NightStartUtc;
            var end = shift == 0 ? w.NightStartUtc : w.EndUtc;
            var totals = new Dictionary<int, decimal?>(); bool complete = true;
            foreach (int counter in query.CounterIndices)
            {
                var rows = result.Records.Where(r => r.CounterIndex == counter && ProductionCalendar.Classify(r, w, now) == shift)
                    .OrderBy(r => r.PeriodStartUtc).ToArray();
                // 有重叠即无法确认该计数器合计；完全没有记录使用 null，不能把缺失数据表达为零产量。
                bool overlap = rows.Zip(rows.Skip(1)).Any(p => p.First.PeriodEndUtc > p.Second.PeriodStartUtc);
                totals[counter] = rows.Length == 0 || overlap ? null : rows.Sum(r => (decimal)r.Quantity);
                if (overlap) warnings.Add($"产能{counter}存在重叠区间，相关班次合计不可确认。");
                // 首尾贴合且时长总和覆盖整班，再配合无重叠，才能证明时间区间连续覆盖。
                complete &= rows.Length > 0 && !overlap && rows[0].PeriodStartUtc == start && rows[^1].PeriodEndUtc == end &&
                    rows.Sum(r => (r.PeriodEndUtc - r.PeriodStartUtc).Ticks) == (end - start).Ticks;
            }
            // 区间覆盖还不足以确认完整：班次必须已结束、没有非法记录且没有相交的监控缺口。
            complete &= invalid == 0 && now >= end && !ProductionCalendar.HasGap(result.Gaps, start, end);
            string status = now < start ? "未开班" : complete ? "已记录完整" :
                totals.Values.All(n => n is null) ? "暂无可确认记录" : now < end ? "进行中 · 已记录部分" : "记录不完整／待核对";
            shifts.Add(new(shift == 0 ? "Day" : "Night", start, end, totals, status, complete, invalid));
        }
        return result with { Query = query, Shifts = shifts, Warnings = warnings };
    }

    /// <summary>
    /// 为连续日期构建来源对应的窗口，并根据数据库聚合的重叠、覆盖时长及缺口计算每日趋势状态。
    /// </summary>
    public async Task<ProductionTrendResult> QueryTrendAsync(ProductionTrendQuery query, CancellationToken token = default)
    {
        query.Validate(); token.ThrowIfCancellationRequested();
        var windows = Enumerable.Range(0, (query.ToDate - query.FromDate).Days + 1)
            .Select(n => ProductionCapacityCalendar.HistoryWindow(query.FromDate.AddDays(n), query.SourceId)).ToArray();
        var now = DateTime.UtcNow;
        var result = await repository.QueryTrendAsync(query, windows, now, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var days = windows.Select(w =>
        {
            var groups = result.Aggregates.Where(r => r.ProductionDate.Date == w.ProductionDate).ToArray();
            long invalid = groups.Where(r => r.Shift < 0).Sum(r => r.Records);
            var day = groups.SingleOrDefault(r => r.Shift == 0); var night = groups.SingleOrDefault(r => r.Shift == 1);
            // 趋势沿用日明细口径：缺组或重叠时不返回可确认产量，完整性还要检查首尾与覆盖毫秒数。
            decimal? Quantity(ProductionTrendAggregate? g) => g is null || g.Overlaps > 0 ? null : g.Quantity;
            bool Complete(ProductionTrendAggregate? g, DateTime start, DateTime end) =>
                invalid == 0 && g is not null && g.Overlaps == 0 && now >= end && g.FirstUtc == start && g.LastUtc == end &&
                g.CoverageMs == (end - start).Ticks / (decimal)TimeSpan.TicksPerMillisecond &&
                !ProductionCalendar.HasGap(result.Gaps, start, end);
            bool dc = Complete(day, w.StartUtc, w.NightStartUtc), nc = Complete(night, w.NightStartUtc, w.EndUtc);
            string Status(ProductionTrendAggregate? g, DateTime start, bool complete) => now < start ? "未开班" :
                g is null ? "暂无记录" : g.Overlaps > 0 ? "区间重叠，待核对" : complete ? "已记录完整" : "已记录部分／待核对";
            return new ProductionTrendDay(w.ProductionDate, Quantity(day), Quantity(night),
                Status(day, w.StartUtc, dc), Status(night, w.NightStartUtc, nc), dc && nc, invalid);
        }).ToArray();
        return result with { Days = days };
    }
}


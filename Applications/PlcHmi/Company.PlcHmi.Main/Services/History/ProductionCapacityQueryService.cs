using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Main.Services.PLC;
using Company.PlcHmi.Shared.PLC.Capacity;
using Company.PlcHmi.Shared.PLC.Capacity.Models;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.History;
using Company.Core.Ioc;
using Company.PlcHmi.Shared.Persistence.Models;

namespace Company.PlcHmi.Main.Services.History;

/// <summary>只读投影。PLC 缓存和已入库历史始终分别查询，不合并、不触发同步或写入。</summary>
[ExposedService(Lifetime.Singleton, typeof(IProductionCapacityQueryService))]
public sealed class ProductionCapacityQueryService(IProductionCapacityService capacity,
    IProductionHistoryQueryService history, IPlcBindingConfigProvider configuration) : IProductionCapacityQueryService
{
    public string DefaultDeviceId => configuration.Current?.Devices.FirstOrDefault(d => d.Enabled && d.Protocol == PlcProtocol.OpcUa)?.Id ?? "";
    public event EventHandler? CacheChanged { add => capacity.Changed += value; remove => capacity.Changed -= value; }

    /// <summary>
    /// 按显式来源读取单项提升机产能：缓存展示软件观察，数据库展示已保存且可归班的历史区间。
    /// </summary>
    public async Task<ProductionCapacityDayResult> QueryDayAsync(ProductionDayQuery query, ProductionCapacityDataSource source,
        CancellationToken token = default)
    {
        query.Validate(); Validate(source); token.ThrowIfCancellationRequested();
        if (!query.CounterIndices.SequenceEqual([0])) throw new ArgumentException("当前只接入提升机单项产能（索引0）。");
        query = query with { CounterIndices = Array.AsReadOnly(new[] { 0 }) };
        // 选择缓存时直接投影当前六班次快照；不会因缓存缺失而改查数据库。
        if (source == ProductionCapacityDataSource.PlcCache)
            return CacheDay(query, capacity.GetSnapshot(query.DeviceId), DateTime.UtcNow);

        var result = await history.QueryDayAsync(query, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); var window = ProductionCapacityCalendar.HistoryWindow(query.ProductionDate, query.SourceId);
        var shifts = result.Shifts.Select(s =>
        {
            int index = s.Code == "Day" ? 0 : 1;
            var rows = result.Records.Where(r => r.CounterIndex == 0 && ProductionCalendar.Classify(r, window, result.QueriedUtc) == index)
                .OrderBy(r => r.PeriodStartUtc).ToArray();
            bool overlap = rows.Zip(rows.Skip(1)).Any(p => p.First.PeriodEndUtc > p.Second.PeriodStartUtc);
            var periods = rows.Select(r => new ProductionCapacityPeriod(r.PeriodStartUtc, r.PeriodEndUtc, r.Quantity,
                overlap ? "区间重叠，待核对" : "已完结", r.SourceId + "@" + r.SourceEpoch + " r" + r.SourceRevision, !overlap)).ToArray();
            return new ProductionCapacityShiftResult(s.Code, s.StartUtc, s.EndUtc, s.Totals.GetValueOrDefault(0), null, s.Status, periods);
        }).ToArray();
        // 数据库中的非法归班记录单列待核对明细，缺口只提示，不按邻近计数补算。
        var warnings = result.Warnings.Concat(result.Gaps.Count > 0 ? new[] { $"存在{result.Gaps.Count}条产能来源缺口；缺失产量不推算。" } : []).ToList();
        var invalid = result.Records.Where(r => ProductionCalendar.Classify(r, window, result.QueriedUtc) < 0).ToArray();
        warnings.AddRange(invalid.Take(10).Select(r => $"待核对：{r.ShiftCode} / {ProductionDisplayFormatter.Time(r.PeriodStartUtc, source, query.SourceId)} → {ProductionDisplayFormatter.Time(r.PeriodEndUtc, source, query.SourceId)} / {r.Quantity}件"));
        return new(query, source, result.QueriedUtc,
            "已入库历史：只统计可确认的已保存区间；无记录不代表零产量。",
            "已入库历史 · " + ProductionDisplayFormatter.Synchronization(result.SyncStates, result.QueriedUtc, query.SourceId), shifts, warnings.AsReadOnly());
    }

    /// <summary>
    /// 按相同数据来源生成日期趋势；缓存趋势保留软件观测身份，不宣称已完整入库。
    /// </summary>
    public async Task<ProductionCapacityTrendResult> QueryTrendAsync(ProductionTrendQuery query, ProductionCapacityDataSource source,
        CancellationToken token = default)
    {
        query.Validate(); Validate(source); token.ThrowIfCancellationRequested();
        if (query.CounterIndex != 0) throw new ArgumentException("当前只接入提升机单项产能（索引0）。");
        if (source == ProductionCapacityDataSource.DatabaseHistory)
        {
            var result = await history.QueryTrendAsync(query, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            return new(query, source, result.QueriedUtc, "已入库历史：累计仅包含可确认的记录，缺失班次不补零。",
                "已入库历史 · " + ProductionDisplayFormatter.Synchronization(result.SyncStates, result.QueriedUtc, query.SourceId), result.Days,
                result.Gaps.Count > 0 ? [$"存在{result.Gaps.Count}条产能来源缺口。"] : []);
        }
        var snapshot = capacity.GetSnapshot(query.DeviceId); var now = DateTime.UtcNow;
        var days = new List<ProductionTrendDay>();
        for (var date = query.FromDate; date <= query.ToDate; date = date.AddDays(1))
        {
            token.ThrowIfCancellationRequested();
            var day = CacheDay(new(query.DeviceId, date, [0]), snapshot, now);
            var d = day.Shifts.Single(s => s.Code == "Day"); var n = day.Shifts.Single(s => s.Code == "Night");
            // 软件观测值不标记为已入库完整班次。
            days.Add(new(date, d.Quantity, n.Quantity, d.Status, n.Status, false, 0));
        }
        return new(query, source, now, "PLC缓存：仅覆盖六班次；累计包含已完结及进行中小时的观测值，未入库。",
            SourceStatus(snapshot), days.AsReadOnly(), snapshot.Warnings.Concat(new[] { "缓存以外日期显示 —；不会自动查询数据库补齐。" }).ToArray());
    }

    /// <summary>
    /// 将六班次快照映射到指定生产日；仅把未过期且已开始的小时计入缓存合计，当前小时也参与观察累计。
    /// </summary>
    private static ProductionCapacityDayResult CacheDay(ProductionDayQuery query, ProductionCapacitySnapshot snapshot, DateTime now)
    {
        var window = ProductionCapacityCalendar.Window(query.ProductionDate); var warnings = snapshot.Warnings.ToList();
        var shifts = Enumerable.Range(0, 2).Select(index =>
        {
            string code = index == 0 ? "Day" : "Night";
            var start = index == 0 ? window.StartUtc : window.NightStartUtc; var end = index == 0 ? window.NightStartUtc : window.EndUtc;
            var shift = snapshot.Shifts.SingleOrDefault(s => s.Code == code && s.StartUtc == start && s.EndUtc == end);
            // 未来班次尚未进入缓存时，仍展示预定小时区间；不使用 PLC 预填的产量。
            if (shift is null && snapshot.IsUsable && now < start)
                return new ProductionCapacityShiftResult(code, start, end, null, null, "待开始（/）",
                    Enumerable.Range(0, 12).Select(hour => new ProductionCapacityPeriod(start.AddHours(hour),
                        start.AddHours(hour + 1), null, "/", "待开始 · 计划区间", false)).ToArray());
            if (shift is null || snapshot.State is not (ProductionCapacityState.Ready or ProductionCapacityState.Stale))
                return new ProductionCapacityShiftResult(code, start, end, null, null,
                    snapshot.IsUsable ? now < start ? "未开班／未缓存" : "不在六班次缓存中" : snapshot.Message, []);
            // Stale 状态仍可展示最后观测值，但 IsUsable 为 false 时所有小时都不进入合计。
            bool stale = !snapshot.IsUsable;
            bool mismatch = end <= now && shift.Hours.Sum(h => (decimal)h.Quantity) != shift.PlcQuantity;
            // 未来小时隐藏预填产量；当前小时与已完结小时区分显示，Included 只说明本次展示可累计。
            var periods = shift.Hours.Select(h =>
            {
                bool future = h.StartUtc > now, current = h.StartUtc <= now && h.EndUtc > now;
                string state = future ? "/" : current ? "进行中" : "已完结";
                return new ProductionCapacityPeriod(h.StartUtc, h.EndUtc, future ? null : h.Quantity, state,
                    $"PLC缓存 · 槽位{shift.Slot}/{h.Slot}" + (stale ? " · 过期／最后观测值" : ""), !future && !stale);
            }).ToArray();
            // 采用 decimal 汇总原生 uint 小时值，避免整班求和时出现 UInt32 溢出。
            var included = periods.Where(p => p.Included).ToArray();
            decimal? quantity = included.Length == 0 ? null : included.Sum(p => p.Quantity!.Value);
            string status = stale ? "数据过期，不计入合计" : now < start ? "待开始（/）" : mismatch ? "已完结 · PLC班次累计与小时之和不一致，按小时求和" :
                now < end ? "进行中 · 累计包含当前小时" : "已完结 · 软件观测值";
            // 当前班次累计由小时求和，PLC整班计数仅在结班核对后展示，避免把启动时的旧值当作实时值。
            return new ProductionCapacityShiftResult(code, start, end, quantity, end <= now ? shift.PlcQuantity : null, status, periods);
        }).ToArray();
        return new(query, ProductionCapacityDataSource.PlcCache, now,
            snapshot.IsUsable ? "提升机 · PLC缓存只读；白班、夜班均按小时求和，包含进行中小时。" : snapshot.Message,
            SourceStatus(snapshot), shifts, warnings.AsReadOnly());
    }
    // 状态文字附最后观察时刻及未入库身份，避免把缓存新鲜度误读为数据库同步状态。
    private static string SourceStatus(ProductionCapacitySnapshot snapshot) => "PLC缓存 · " + snapshot.Message +
        (snapshot.ObservedAt is { } at ? " 最后观测（北京时间） " + ProductionDisplayFormatter.Time(at.UtcDateTime, ProductionCapacityDataSource.PlcCache) : "") + " · 未入库";
    /// <summary>
    /// 拒绝未定义的数据来源枚举值，保证查询路径与界面标签一致。
    /// </summary>
    private static void Validate(ProductionCapacityDataSource source)
    { if (!Enum.IsDefined(source)) throw new ArgumentException("请选择有效数据来源。"); }
}

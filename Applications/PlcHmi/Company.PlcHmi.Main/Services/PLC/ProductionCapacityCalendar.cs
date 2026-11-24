using Company.PlcHmi.Main.Services.History;
using Company.PlcHmi.Shared.PLC.Capacity.Models;
using Company.PlcHmi.Shared.Persistence.Models;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>PLC产能缓存专用日历；不改变已入库历史使用的旧日历。</summary>
public static class ProductionCapacityCalendar
{
    public const string ZoneId = "Asia/Shanghai";
    public static TimeZoneInfo Zone { get; } = BeijingZone();

    /// <summary>
    /// 优先使用 IANA 北京时区标识，兼容只提供 Windows 中国标准时间标识的系统。
    /// </summary>
    private static TimeZoneInfo BeijingZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    /// <summary>
    /// 仅将明确 UTC 的内部时间转换为北京时间，拒绝依赖 DateTime.Kind 猜测来源。
    /// </summary>
    public static DateTime ToBeijing(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("产能内部时间必须为UTC。");
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
    }
    /// <summary>
    /// 以北京时间 08:00 为生产日切点；凌晨时段归入前一生产日夜班。
    /// </summary>
    public static DateTime CurrentProductionDate(DateTime utc) => ToBeijing(utc).AddHours(-8).Date;
    /// <summary>
    /// 按数据来源及历史来源标识选用产能专用日历或旧历史日历，保持已保存数据的原有解释口径。
    /// </summary>
    public static ProductionCapacityDataSource CalendarFor(ProductionCapacityDataSource source, string? historySource) =>
        source == ProductionCapacityDataSource.PlcCache || ProductionSourcePolicy.IsCapacity(historySource) ? ProductionCapacityDataSource.PlcCache : ProductionCapacityDataSource.DatabaseHistory;
    /// <summary>
    /// 为当前来源计算默认生产日期，避免提升机来源因本机时区不同而跨日错位。
    /// </summary>
    public static DateTime DateFor(ProductionCapacityDataSource source, DateTime utc, string? historySource = null) =>
        CalendarFor(source, historySource) == ProductionCapacityDataSource.PlcCache ? CurrentProductionDate(utc) : ProductionCalendar.CurrentProductionDate(utc);
    /// <summary>
    /// CapacityDay 类历史使用北京时间 08:00 窗口，其余历史继续使用旧日历。
    /// </summary>
    public static ProductionDayWindow HistoryWindow(DateTime date, string? historySource) =>
        ProductionSourcePolicy.IsCapacity(historySource) ? Window(date) : ProductionCalendar.Window(date);

    /// <summary>
    /// 建立北京时间当日 08:00、20:00 及次日 08:00 的 UTC 边界。
    /// </summary>
    public static ProductionDayWindow Window(DateTime date)
    {
        DateTime Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
        return new(date.Date, Utc(date.Date.AddHours(8)), Utc(date.Date.AddHours(20)), Utc(date.Date.AddDays(1).AddHours(8)));
    }

    /// <summary>
    /// 严格核对完整十二小时班次边界，仅接受白班或夜班标准窗口。
    /// </summary>
    internal static (string Code, DateTime ProductionDate) ClassifyShift(DateTime startUtc, DateTime endUtc)
    {
        var date = CurrentProductionDate(startUtc); var window = Window(date);
        if (startUtc == window.StartUtc && endUtc == window.NightStartUtc) return ("Day", date);
        if (startUtc == window.NightStartUtc && endUtc == window.EndUtc) return ("Night", date);
        throw new ArgumentException("班次时间不符合北京时间08:00/20:00边界或十二小时长度。");
    }

    // 这些文案按日历口径枚举显示；调用方应先通过 CalendarFor 决定当前来源对应的口径。
    public static string DayHeader(ProductionCapacityDataSource source) => source == ProductionCapacityDataSource.PlcCache ?
        "白班 · 08:00 — 20:00（北京时间）" : "白班 · 08:30 — 20:30（原历史口径）";
    public static string NightHeader(ProductionCapacityDataSource source) => source == ProductionCapacityDataSource.PlcCache ?
        "夜班 · 20:00 — 次日08:00（北京时间）" : "夜班 · 20:30 — 次日08:30（原历史口径）";
    public static string Description(ProductionCapacityDataSource source) => source == ProductionCapacityDataSource.PlcCache ?
        "提升机生产日：北京时间当日08:00至次日08:00，白班08:00—20:00，夜班20:00—次日08:00。" :
        "已入库历史沿用原08:30/20:30查询口径及原本机时区显示，不改写旧记录。";
}

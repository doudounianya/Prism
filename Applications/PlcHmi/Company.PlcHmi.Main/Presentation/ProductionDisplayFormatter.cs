/*
 * 文件职责与维护说明：
 * 统一生产页面的数量、数据来源及时间文字。
 * 不同来源仍采用对应生产日口径；格式化不改变数据库结果，也不把无记录转换成有效零产量。
 */
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.PLC.Capacity.Models;
using Company.PlcHmi.Main.Services.PLC;
using System.Globalization;
namespace Company.PlcHmi.Main.Presentation;

public static class ProductionDisplayFormatter
{
    public static string Source(ProductionCapacityDataSource source) => source == ProductionCapacityDataSource.PlcCache ? "PLC缓存" : "已入库历史";
    public static string Period(DateTime? start, DateTime? end) => start is { } s && end is { } e ?
        DateTime.SpecifyKind(s, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") + " → " +
        DateTime.SpecifyKind(e, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") : "—";
    public static string Quantity(decimal? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    public static string Time(DateTime? utc) => utc is { } value ?
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";
    private static DateTime DatabaseUtc(DateTime utc, ProductionCapacityDataSource source) => source == ProductionCapacityDataSource.DatabaseHistory ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc;
    public static string Time(DateTime? utc, ProductionCapacityDataSource source, string? historySource = null) => ProductionCapacityCalendar.CalendarFor(source, historySource) == ProductionCapacityDataSource.PlcCache ?
        utc is { } value ? ProductionCapacityCalendar.ToBeijing(DatabaseUtc(value, source)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—" : Time(utc);
    public static string Period(DateTime? start, DateTime? end, ProductionCapacityDataSource source, string? historySource = null) => ProductionCapacityCalendar.CalendarFor(source, historySource) == ProductionCapacityDataSource.PlcCache ?
        start is { } s && end is { } e ? ProductionCapacityCalendar.ToBeijing(DatabaseUtc(s, source)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " → " +
            ProductionCapacityCalendar.ToBeijing(DatabaseUtc(e, source)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "—" : Period(start, end);
    public static string Synchronization(IReadOnlyList<PlcProductionSyncState> states, DateTime queriedUtc, string? historySource = null)
    {
        string prefix = "查询时间 " + Time(queriedUtc, ProductionCapacityDataSource.DatabaseHistory, historySource) + " · ";
        if (states.Count == 0) return prefix + "尚无同步状态记录；页面只查询已入库数据。";
        return prefix + string.Join("；", states.Select(s => s.SourceId + "：状态 " + s.Status +
            "，最后成功 " + Time(s.LastSuccessUtc, ProductionCapacityDataSource.DatabaseHistory, historySource) + (s.Rejected > 0 ? $"，有{s.Rejected}条未采纳记录" : "")));
    }
}


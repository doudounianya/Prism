using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.Persistence.Models;
namespace Company.PlcHmi.Main.Services.History;

/// <summary>当前已确定的业务日历：本地08:30开生产日，20:30交夜班。仅影响查询，不重写PLC记录。</summary>
public static class ProductionCalendar
{
    /// <summary>
    /// 沿用旧历史口径，在本机时区内减去 08:30 偏移，得到当前归属生产日。
    /// </summary>
    public static DateTime CurrentProductionDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).AddHours(-8.5).Date;

    /// <summary>
    /// 以指定时区或本机时区建立 08:30/20:30 班次边界，并拒绝夏令时无效或歧义时刻。
    /// </summary>
    public static ProductionDayWindow Window(DateTime date, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        DateTime Utc(DateTime value)
        {
            var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                throw new ArgumentException("生产班次边界处于夏令时跳变区间，请核对时区。");
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        return new(date.Date, Utc(date.Date.AddHours(8.5)), Utc(date.Date.AddHours(20.5)), Utc(date.Date.AddDays(1).AddHours(8.5)));
    }

    /// <summary>
    /// 依据生产日期、时段及班次别名归类：白班为 0、夜班为 1，不一致或未结束的记录为 -1。
    /// </summary>
    public static int Classify(PlcProductionHour row, ProductionDayWindow w, DateTime asOfUtc)
    {
        if (row.ProductionDate.Date != w.ProductionDate || row.PeriodEndUtc <= row.PeriodStartUtc ||
            row.PeriodEndUtc - row.PeriodStartUtc > TimeSpan.FromHours(1) || row.PeriodEndUtc > asOfUtc) return -1;
        var code = row.ShiftCode.Trim().ToUpperInvariant();
        if (row.PeriodStartUtc >= w.StartUtc && row.PeriodEndUtc <= w.NightStartUtc && code is "D" or "DAY" or "白班") return 0;
        if (row.PeriodStartUtc >= w.NightStartUtc && row.PeriodEndUtc <= w.EndUtc && code is "N" or "NIGHT" or "夜班") return 1;
        return -1;
    }

    /// <summary>
    /// 按半开区间相交判断班次是否有缺口；尚未恢复的缺口持续覆盖其开始之后的查询范围。
    /// </summary>
    public static bool HasGap(IEnumerable<PlcMonitoringGap> gaps, DateTime start, DateTime end) =>
        gaps.Any(g => g.StartUtc < end && (g.RecoveredUtc is null || g.RecoveredUtc > start));
}


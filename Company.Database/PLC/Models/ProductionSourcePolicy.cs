namespace Company.Database.PLC.Models;

/// <summary>PLC产能业务来源；不属于通用数据库或框架配置。</summary>
public static class ProductionSourcePolicy
{
    public const string CapacityDay = "CapacityDay0.Observed";
    public const string CapacityGapPrefix = CapacityDay + "|";
    public static bool IsCapacity(string? source) => source == CapacityDay;
    public static void Validate(string? source)
    {
        if (source is not null && !IsCapacity(source)) throw new ArgumentException("产能历史来源无效。");
    }
    public static DateTime Milliseconds(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}

namespace Company.FlyScan.Runtime;

public sealed record ProductionHour(DateTimeOffset From, DateTimeOffset To, int Pieces, bool DayShift);
public sealed record ProductionDay(DateOnly Date, IReadOnlyList<ProductionHour> Hours)
{
    public int DayPieces => Hours.Where(hour => hour.DayShift).Sum(hour => hour.Pieces);
    public int NightPieces => Hours.Where(hour => !hour.DayShift).Sum(hour => hour.Pieces);
    public int Total => DayPieces + NightPieces;
}

/// <summary>按北京时间八点切生产日，二十点切夜班。以工件确认完成时间归属班次。</summary>
public static class FlyProductionCalendar
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public static DateTimeOffset Local(DateTimeOffset time) => time.ToOffset(Offset);
    public static DateOnly DayOf(DateTimeOffset time) => DateOnly.FromDateTime(Local(time).DateTime.AddHours(-8));
    public static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(new TimeOnly(8, 0)), Offset);

    public static IReadOnlyList<ProductionDay> Read(FlyScanHistory history, DateOnly first, DateOnly last)
    {
        int days = last.DayNumber - first.DayNumber + 1;
        if (days is < 1 or > 92 || last == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(last), "请选择 1～92 个生产日。");
        var begin = StartOf(first);
        var end = StartOf(last.AddDays(1));
        var counts = history.Pieces(begin, end)
            .GroupBy(piece => (int)((piece.Time - begin).Ticks / TimeSpan.TicksPerHour))
            .ToDictionary(group => group.Key, group => group.Count());
        return Enumerable.Range(0, days).Select(day => new ProductionDay(first.AddDays(day),
            Enumerable.Range(0, 24).Select(hour =>
            {
                int index = day * 24 + hour;
                return new ProductionHour(begin.AddHours(index), begin.AddHours(index + 1), counts.GetValueOrDefault(index), hour < 12);
            }).ToArray())).ToArray();
    }
}

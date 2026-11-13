namespace Company.Database.PLC.Models;

public sealed record ProductionDayQuery(string DeviceId, DateTime ProductionDate, IReadOnlyList<int> CounterIndices)
{
    public string? SourceId { get; init; }
    public void Validate()
    {
        ProductionSourcePolicy.Validate(SourceId);
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.Length > 64)
            throw new ArgumentException("请输入设备编号（不超过64个字符）。");
        if (ProductionDate.TimeOfDay != TimeSpan.Zero || ProductionDate.Year < 2000 || ProductionDate.Year > 2098)
            throw new ArgumentException("生产日期应为2000至2098年内的日期。");
        if (CounterIndices is null || CounterIndices.Count is < 1 or > 3 ||
            CounterIndices.Any(n => n is < 0 or > 9) || CounterIndices.Distinct().Count() != CounterIndices.Count)
            throw new ArgumentException("请选择1至3个不同的产能编号（0–9）。");
    }
}


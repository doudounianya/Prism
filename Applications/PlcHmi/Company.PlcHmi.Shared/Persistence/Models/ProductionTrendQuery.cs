namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed record ProductionTrendQuery(string DeviceId, DateTime FromDate, DateTime ToDate, int CounterIndex)
{
    public string? SourceId { get; init; }
    public void Validate()
    {
        ProductionSourcePolicy.Validate(SourceId);
        new ProductionDayQuery(DeviceId, FromDate, [CounterIndex]).Validate();
        new ProductionDayQuery(DeviceId, ToDate, [CounterIndex]).Validate();
        if (ToDate < FromDate || (ToDate - FromDate).TotalDays >= 366)
            throw new ArgumentException("结束日期不能早于开始日期，单次最多查询366个生产日。");
    }
}


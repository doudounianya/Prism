using Company.PlcHmi.Shared.Persistence.Entities;
namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed class ProductionTrendAggregate
{
    public DateTime ProductionDate { get; set; }
    public int Shift { get; set; }
    public decimal Quantity { get; set; }
    public long Records { get; set; }
    public long Overlaps { get; set; }
    public decimal CoverageMs { get; set; }
    public DateTime? FirstUtc { get; set; }
    public DateTime? LastUtc { get; set; }
}
public sealed record ProductionTrendDay(DateTime ProductionDate, decimal? DayQuantity, decimal? NightQuantity,
    string DayStatus, string NightStatus, bool Complete, long InvalidRecords)
{
    public decimal? RecordedTotal => DayQuantity is null && NightQuantity is null ? null : (DayQuantity ?? 0) + (NightQuantity ?? 0);
}
public sealed record ProductionTrendResult(ProductionTrendQuery Query, IReadOnlyList<ProductionTrendAggregate> Aggregates,
    IReadOnlyList<PlcProductionSyncState> SyncStates, IReadOnlyList<PlcMonitoringGap> Gaps, DateTime QueriedUtc)
{
    public IReadOnlyList<ProductionTrendDay> Days { get; init; } = [];
}


using Company.PlcHmi.Shared.Persistence.Entities;
namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed record ProductionDayWindow(DateTime ProductionDate, DateTime StartUtc, DateTime NightStartUtc, DateTime EndUtc);
public sealed record ProductionShiftResult(string Code, DateTime StartUtc, DateTime EndUtc,
    IReadOnlyDictionary<int, decimal?> Totals, string Status, bool Complete, int InvalidRecords);
public sealed record ProductionDayResult(ProductionDayQuery Query, IReadOnlyList<PlcProductionHour> Records,
    IReadOnlyList<PlcProductionSyncState> SyncStates, IReadOnlyList<PlcMonitoringGap> Gaps, DateTime QueriedUtc)
{
    public IReadOnlyList<ProductionShiftResult> Shifts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}


using Company.PlcHmi.Shared.Persistence.Entities;
namespace Company.PlcHmi.Shared.Persistence.Models;

public sealed record AlarmHistoryDetail(PlcAlarmEvent Event, IReadOnlyList<PlcAlarmSegment> Segments,
    decimal ObservedDurationMs);

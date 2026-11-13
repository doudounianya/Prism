using Company.Database.PLC.Entities;
namespace Company.Database.PLC.Models;

public sealed record AlarmHistoryDetail(PlcAlarmEvent Event, IReadOnlyList<PlcAlarmSegment> Segments,
    decimal ObservedDurationMs);

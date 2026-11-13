namespace Company.Hardware.PLC.Models;

public enum PlcDataQuality { Unknown, Good, Uncertain, Bad }

/// <summary>无效数据不产生默认业务值；HasValue 与质量需同时判断。</summary>
public sealed record PlcReadResult(
    PlcPoint Point, object? Value, Type? ActualType, bool HasValue,
    PlcDataQuality Quality, uint? NativeStatusCode,
    DateTimeOffset? SourceTimestamp, DateTimeOffset? ServerTimestamp,
    DateTimeOffset ReceivedAt, PlcError? Error) : IPlcItemResult
{
    public bool Success => HasValue && Quality == PlcDataQuality.Good && Error is null;
}

namespace Company.Hardware.PLC.Models;

/// <summary>Value 必须与点位预期类型完全匹配，不执行隐式数值转换。</summary>
public sealed record PlcWriteRequest(PlcPoint Point, object? Value);

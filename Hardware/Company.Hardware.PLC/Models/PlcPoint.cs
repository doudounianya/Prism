namespace Company.Hardware.PLC.Models;

/// <summary>RequestId 在一次批量请求中唯一。ExpectedType 为 PLC 原生值类型，不是界面显示类型。</summary>
public sealed record PlcPoint(string RequestId, string LogicalId, PlcAddress Address, Type ExpectedType);

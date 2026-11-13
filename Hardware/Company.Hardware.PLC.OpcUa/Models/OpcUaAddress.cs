using Company.Hardware.PLC.Models;

namespace Company.Hardware.PLC.OpcUa.Models;

/// <summary>NodeIdentifier 例如 s=Device.Value 或 i=123，不含 ns=；ArrayIndex 从 0 开始。</summary>
public sealed record OpcUaAddress(string NamespaceUri, string NodeIdentifier, int? ArrayIndex = null) : PlcAddress;

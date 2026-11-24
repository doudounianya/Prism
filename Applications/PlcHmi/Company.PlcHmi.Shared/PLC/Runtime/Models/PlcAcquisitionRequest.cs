namespace Company.PlcHmi.Shared.PLC.Runtime.Models;

/// <summary>明确列出实际显示的变量。Groups 只作范围约束，不会将空 Variables 扩展成整组。</summary>
/// <remarks>OwnerId 在范围生命周期内保持不变；Groups 非空时仅用于校验 Variables 的归属，不会自动枚举组内全部地址。</remarks>
public sealed record PlcAcquisitionRequest(
    string OwnerId, IReadOnlyCollection<string> Groups, IReadOnlyCollection<PlcVariableKey> Variables);

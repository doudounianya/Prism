namespace Company.Hardware.PLC.Models;

public enum PlcWriteState { NotSent, Succeeded, Rejected, OutcomeUnknown }

/// <summary>Succeeded 仅代表服务端接受写入，不代表 PLC 动作或业务握手完成。</summary>
public sealed record PlcWriteResult(
    PlcPoint Point, PlcWriteState State, uint? NativeStatusCode,
    DateTimeOffset CompletedAt, PlcError? Error) : IPlcItemResult
{
    public bool Success => State == PlcWriteState.Succeeded && Error is null;
}

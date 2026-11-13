namespace Company.Hardware.PLC.Models;

public enum PlcErrorKind
{
    InvalidRequest, InvalidAddress, TypeMismatch, NotConnected, Timeout,
    Cancelled, Communication, ServerRejected, InvalidResponse, Disposed
}

/// <summary>不携带 SDK 异常对象、身份凭据或完整连接配置。</summary>
public sealed record PlcError(PlcErrorKind Kind, string Message, uint? NativeStatusCode = null);

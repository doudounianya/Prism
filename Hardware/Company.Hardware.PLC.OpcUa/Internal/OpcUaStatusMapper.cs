using Company.Hardware.PLC.Models;
using Opc.Ua;

namespace Company.Hardware.PLC.OpcUa.Internal;

internal static class OpcUaStatusMapper
{
    internal static PlcDataQuality Quality(StatusCode status) =>
        StatusCode.IsBad(status) ? PlcDataQuality.Bad :
        StatusCode.IsUncertain(status) ? PlcDataQuality.Uncertain : PlcDataQuality.Good;

    internal static DateTimeOffset? Timestamp(DateTime value) => value == DateTime.MinValue ? null :
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static PlcError Server(StatusCode status) =>
        new(PlcErrorKind.ServerRejected, "OPC UA 服务端返回：" + status, status.Code);

    internal static PlcError Exception(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException cancelledException)
        {
            var nativeCode = (cancelledException.InnerException as ServiceResultException)?.StatusCode;
            return callerToken.IsCancellationRequested
                ? new(PlcErrorKind.Cancelled, "通讯等待已取消，已发送的写入不能据此判断未执行。", nativeCode)
                : new(PlcErrorKind.Timeout, "OPC UA 通讯超时。", nativeCode);
        }
        if (exception is TimeoutException)
            return new(PlcErrorKind.Timeout, "OPC UA 通讯超时。");
        if (exception is ServiceResultException service)
        {
            // SDK 使用取消请求状态码表达 CancellationToken 取消，也可能用于内部超时。
            var cancelled = service.StatusCode is StatusCodes.BadRequestCancelledByClient or StatusCodes.BadRequestCancelledByRequest or StatusCodes.BadRequestInterrupted;
            var kind = cancelled ? (callerToken.IsCancellationRequested ? PlcErrorKind.Cancelled : PlcErrorKind.Timeout) :
                service.StatusCode is StatusCodes.BadTimeout or StatusCodes.BadRequestTimeout ? PlcErrorKind.Timeout : PlcErrorKind.Communication;
            return new(kind, "OPC UA 通讯失败：" + new StatusCode(service.StatusCode), service.StatusCode);
        }
        return new(PlcErrorKind.Communication, "OPC UA 通讯失败。");
    }
}

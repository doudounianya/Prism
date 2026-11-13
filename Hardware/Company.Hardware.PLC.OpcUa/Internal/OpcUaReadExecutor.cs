using Company.Hardware.PLC.Models;
using Opc.Ua;

namespace Company.Hardware.PLC.OpcUa.Internal;

/// <summary>只读请求的有界拆批；不参与写入，不把请求大小拒绝当成断线。</summary>
internal sealed class OpcUaReadExecutor
{
    internal int Limit { get; private set; } = 100;
    internal void Reset(int limit) => Limit = Math.Max(1, limit);

    private static bool SizeRejected(uint code) => code is StatusCodes.BadTooManyOperations or
        StatusCodes.BadRequestTooLarge or StatusCodes.BadResponseTooLarge or StatusCodes.BadEncodingLimitsExceeded;
    private static bool RangeRejected(uint code) => code is StatusCodes.BadIndexRangeInvalid or StatusCodes.BadIndexRangeNoData;

    internal async Task<PlcError?> ExecuteAsync(IReadOnlyList<OpcUaReadTarget> targets,
        Func<ReadValueIdCollection, CancellationToken, Task<DataValueCollection>> read,
        Action<OpcUaReadTarget, DataValue> accept, CancellationToken token)
    {
        var pending = new List<OpcUaReadTarget>(targets);
        PlcError? error = null;
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(Limit, pending.Count);
            var chunk = pending.Take(count).ToArray();
            DataValueCollection values;
            try
            {
                values = await read(new ReadValueIdCollection(chunk.Select(t => t.ToReadValue())), token).ConfigureAwait(false);
                if (values.Count != count) throw new ServiceResultException(StatusCodes.BadUnexpectedError, "读取响应数量不匹配。");
            }
            catch (ServiceResultException ex) when (SizeRejected(ex.StatusCode) || RangeRejected(ex.StatusCode))
            {
                if (count > 1)
                {
                    Limit = Math.Max(1, count / 2);
                    continue;
                }
                if (chunk[0].Start is not null && chunk[0].Length > 1)
                {
                    pending.RemoveAt(0);
                    pending.InsertRange(0, chunk[0].Split(RangeRejected(ex.StatusCode)));
                    continue;
                }
                error ??= OpcUaStatusMapper.Server(ex.StatusCode);
                // 请求无法再缩小时仅此项失败，仍尝试其他独立点；保留服务端状态码。
                accept(chunk[0], new DataValue { StatusCode = ex.StatusCode });
                pending.RemoveAt(0);
                continue;
            }
            pending.RemoveRange(0, count);
            var retry = new List<OpcUaReadTarget>();
            for (int i = 0; i < count; i++)
            {
                var target = chunk[i]; var value = values[i];
                if (target.Start is not null && target.Length > 1 &&
                    (RangeRejected(value.StatusCode.Code) || SizeRejected(value.StatusCode.Code)))
                    retry.AddRange(target.Split(RangeRejected(value.StatusCode.Code)));
                else
                {
                    if (SizeRejected(value.StatusCode.Code)) error ??= OpcUaStatusMapper.Server(value.StatusCode);
                    accept(target, value);
                }
            }
            pending.InsertRange(0, retry);
        }
        return error;
    }
}

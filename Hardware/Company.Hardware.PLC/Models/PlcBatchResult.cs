namespace Company.Hardware.PLC.Models;

public interface IPlcItemResult { bool Success { get; } }

/// <summary>Results 顺序与输入一致；Error 表示批次级错误，单点错误见各结果。</summary>
public sealed class PlcBatchResult<T> where T : IPlcItemResult
{
    public PlcBatchResult(IEnumerable<T> results, DateTimeOffset startedAt,
        DateTimeOffset completedAt, PlcError? error = null)
    {
        Results = Array.AsReadOnly(results.ToArray());
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Error = error;
    }
    public IReadOnlyList<T> Results { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }
    public PlcError? Error { get; }
    public bool AllSucceeded => Error is null && Results.All(item => item.Success);
}

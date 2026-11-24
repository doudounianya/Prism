using Company.PlcHmi.Shared.PLC.Runtime.Models;

namespace Company.PlcHmi.Shared.PLC.Monitoring.Models;

/// <summary>单个 PLC 原始计数器的运行观测；保留当前值、历史有效值和缺口边界，避免跨断线推算增量。</summary>
public sealed record ProductionCounterSnapshot
{
    public required PlcVariableKey Key { get; init; }
    public int CounterIndex { get; init; }
    public string DisplayName => $"产能{CounterIndex}";
    /// <summary>当前可信的原始无符号计数；未知状态用空值表示。</summary>
    public uint? Value { get; init; }
    /// <summary>最近一次有效原始计数，仅供追溯，不能替代缺口期间的当前值。</summary>
    public uint? LastValidValue { get; init; }
    public PlcValueState State { get; init; } = PlcValueState.Pending;
    public PlcObservationQuality Quality { get; init; }
    public DateTimeOffset? LastValidAt { get; init; }
    public DateTimeOffset? LastSampleAt { get; init; }
    /// <summary>最近一次监控缺口边界；晚到的旧采集帧不能恢复当前状态。</summary>
    public DateTimeOffset? LastGapAt { get; init; }
    /// <summary>首次失去连续有效观测的时间边界，用于解释当前计数未知。</summary>
    public DateTimeOffset? UnknownSince { get; init; }
    public long ConnectionEpoch { get; init; }
    public string ConfigurationVersion { get; init; } = "";
    /// <summary>计数基线及变化的业务说明，帮助界面解释重置、重连或尚未建立基线等情况。</summary>
    public string Observation { get; init; } = "尚未建立计数基线";
    public string? Error { get; init; }
    public string ValueText => Value?.ToString("N0") ?? "—";
    public string LastValueText => LastValidValue?.ToString("N0") ?? "—";
    public string StateText => State == PlcValueState.Valid ? "有效" : State == PlcValueState.Pending ? "等待读取" : "未知";
    public string UpdatedText => LastValidAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
}

namespace Company.Application.Share.PLC.Runtime.Models;

/// <summary>当前值状态；Pending 等待尝试，Valid 有效，Uncertain 不确定，Invalid 无效，Stale 超时，Inactive 无采集需求。</summary>
public enum PlcValueState { Pending, Valid, Uncertain, Invalid, Stale, Inactive }
/// <summary>保留通讯层观测质量，与当前值状态共同解释样本，不能只凭存在数值认定可信。</summary>
public enum PlcObservationQuality { Unknown, Good, Uncertain, Bad }

/// <summary>变量最新观测的不可变快照；当前值与最近有效历史值分开保存，并携带时间、质量、配置及连接边界。</summary>
public sealed record PlcVariableSnapshot
{
    public required PlcVariableKey Key { get; init; }
    /// <summary>本轮通讯返回的原始值；当前断线、停用或超时可能被清空。</summary>
    public object? RawValue { get; init; }
    /// <summary>本轮通过有效性和工程量转换的当前值；无效时为空，界面应结合 State 展示。</summary>
    public object? EngineeringValue { get; init; }
    /// <summary>最近有效的工程量，即使当前值失效也保留；不得直接当作实时值使用。</summary>
    public object? LastValidValue { get; init; }
    public PlcValueState State { get; init; } = PlcValueState.Inactive;
    public PlcObservationQuality Quality { get; init; }
    /// <summary>协议原生状态码，例如 OPC UA 状态码，用于诊断而非替代统一质量状态。</summary>
    public uint? NativeStatusCode { get; init; }
    public string? Error { get; init; }
    /// <summary>最近一次接纳的读取尝试接收时间，失败尝试也可更新。</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }
    /// <summary>最近一次有效工程量的接收时间；本轮失败不覆盖它。</summary>
    public DateTimeOffset? LastValidAt { get; init; }
    /// <summary>服务端返回的源数据时间戳，可能为空，与软件接收时间区分。</summary>
    public DateTimeOffset? SourceTimestamp { get; init; }
    /// <summary>服务端处理或记录该值的时间戳，是否存在取决于协议响应。</summary>
    public DateTimeOffset? ServerTimestamp { get; init; }
    /// <summary>最近一次接纳样本所属的连接代次。</summary>
    public long ConnectionEpoch { get; init; }
    /// <summary>最近有效值所属连接代次，用于识别新会话中的首次有效观测。</summary>
    public long LastValidConnectionEpoch { get; init; }
    public string ConfigurationVersion { get; init; } = "";
    /// <summary>本次有效值属于不同于上次有效值的连接代次；首个成功会话的首值也会置位。</summary>
    public bool IsInitialAfterReconnect { get; init; }
    /// <summary>本次结果有效且所属组为周期采样；OnEnter 的有效值仍不标记为持续实时采集。</summary>
    public bool IsRealtime { get; init; }
    public bool IsValid => State == PlcValueState.Valid;
}

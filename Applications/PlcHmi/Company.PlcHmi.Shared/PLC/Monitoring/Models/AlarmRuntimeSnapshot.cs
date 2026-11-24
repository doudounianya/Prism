using Company.PlcHmi.Shared.PLC.Runtime.Models;

namespace Company.PlcHmi.Shared.PLC.Monitoring.Models;

/// <summary>单报警位的当前运行观测；活动起止为软件观察到的边界，不能解释成 PLC 实际发生时刻。</summary>
public sealed record AlarmRuntimeSnapshot
{
    public required PlcVariableKey Key { get; init; }
    public int AlarmIndex { get; init; }
    /// <summary>本次有效观测的活动状态；为空表示尚未读到或当前状态未知。</summary>
    public bool? Active { get; init; }
    /// <summary>最近一次已知活动状态，用于追溯；当前未知时不能代替 Active 参与实时判断。</summary>
    public bool? LastKnownActive { get; init; }
    public PlcValueState State { get; init; } = PlcValueState.Pending;
    public PlcObservationQuality Quality { get; init; }
    public DateTimeOffset? StartObservedAt { get; init; }
    public DateTimeOffset? EndObservedAt { get; init; }
    /// <summary>观测连续性中断的起点；没有该边界不能推定断线期间报警保持原状态。</summary>
    public DateTimeOffset? UnknownSince { get; init; }
    public DateTimeOffset? LastValidAt { get; init; }
    public DateTimeOffset? LastSampleAt { get; init; }
    /// <summary>最近一次监控缺口边界；晚到的旧采集帧不能恢复当前状态。</summary>
    public DateTimeOffset? LastGapAt { get; init; }
    public string BoundarySource { get; init; } = "尚未取得有效状态";
    public string? Error { get; init; }
    /// <summary>该观测所属的通讯会话代次；会话变化时业务边界须重新建立。</summary>
    public long ConnectionEpoch { get; init; }
    public string ConfigurationVersion { get; init; } = "";
    /// <summary>展示时优先使用三态 Active；空值不会被格式化成未触发。</summary>
    public string StateText => Active is true ? "活动" : Active is false ? "未触发" : State == PlcValueState.Pending ? "等待读取" : "未知";
    public string DefinitionText => "报警定义待现场确认";
    public string StartText => StartObservedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "—";
    public string EndText => EndObservedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "—";
    public string UpdatedText => LastValidAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
}

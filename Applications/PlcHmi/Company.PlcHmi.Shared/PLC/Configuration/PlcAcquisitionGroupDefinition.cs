namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>采集需求生命周期：Background 常驻，Page 随页面范围存在，Unknown 用于识别未配置值。</summary>
public enum PlcAcquisitionLifetime { Unknown, Background, Page }
/// <summary>读取方式：PeriodicBatch 按周期批量读取，OnEnter 在需求进入或主动刷新时尝试。</summary>
public enum PlcAcquisitionMode { Unknown, PeriodicBatch, OnEnter }

/// <summary>Excel 中的采集组策略；运行计划校验生命周期与周期组合后，调度器据此执行读取和失效检查。</summary>
public sealed record PlcAcquisitionGroupDefinition
{
    public required PlcConfigurationSource Source { get; init; }
    public string Id { get; init; } = "";
    public PlcAcquisitionLifetime Lifetime { get; init; }
    public PlcAcquisitionMode Mode { get; init; }
    /// <summary>配置中的激活条件说明；具体需求是否生效由采集范围及后台选择策略决定。</summary>
    public string ActivationCondition { get; init; } = "";
    /// <summary>周期批量读取的目标间隔，单位毫秒；不是任务执行时长的保证。</summary>
    public int? IntervalMs { get; init; }
    /// <summary>周期采样允许的数据新鲜度期限，单位毫秒；超过后当前值被置为 Stale。</summary>
    public int? StaleAfterMs { get; init; }
    /// <summary>后台监控检查点间隔，单位毫秒，随采集帧交给业务消费者。</summary>
    public int? CheckpointIntervalMs { get; init; }
    public string ReleaseRule { get; init; } = "";
    public string Notes { get; init; } = "";
}

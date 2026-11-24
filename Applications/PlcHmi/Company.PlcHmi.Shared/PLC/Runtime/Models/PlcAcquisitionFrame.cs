namespace Company.PlcHmi.Shared.PLC.Runtime.Models;

/// <summary>同轮结果不代表 PLC 业务数据的原子快照。每项质量及首次有效标志须独立判断。</summary>
/// <remarks>StartedAt/CompletedAt 为本轮通讯时间边界，CheckpointIntervalMs 为该组后台检查点策略，样本集合应按只读方式消费。</remarks>
public sealed record PlcAcquisitionFrame(string DeviceId, string GroupId, string ConfigurationVersion,
    long ConnectionEpoch, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<PlcVariableSnapshot> Samples, int? CheckpointIntervalMs);

/// <summary>后台监控连续性缺口，携带设备/组/配置边界及原因；LastValidAt 为空表示无法定位最近有效样本时间。</summary>
public sealed record PlcMonitoringGap(string DeviceId, string GroupId, string ConfigurationVersion,
    DateTimeOffset DetectedAt, DateTimeOffset? LastValidAt, string Reason);

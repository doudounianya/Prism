namespace Company.Hardware.ControlCard;

/// <summary>
/// 将已确认的参数作为固定配置提供给一次显式初始化。
/// 不读写现有 PLC Excel/数据库；修改参数应创建新提供者并关闭旧会话后重新初始化。
/// </summary>
public sealed class ControlCardConfigSnapshotProvider : IControlCardConfigProvider
{
    private readonly ControlCardConfig snapshot;
    public ControlCardConfigSnapshotProvider(ControlCardConfig config)
        => snapshot = (config ?? throw new ArgumentNullException(nameof(config))).Snapshot();
    public ControlCardConfig ControlCardConfig => snapshot.Snapshot();
    // 固定配置不会在运行中广播替换；保留原提供者接口，供现有容器及调用方兼容使用。
    public event Action OnChanged { add { } remove { } }
}

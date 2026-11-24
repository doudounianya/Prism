namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>厂商能力必须分别确认，不能把允许原生访问等同于批准覆盖参数和修改接线极性。</summary>
public sealed class InovanceNativeOptions
{
    /// <summary>明确的零基卡号，不会静默改选其他卡。</summary>
    public int CardNumber { get; set; }
    /// <summary>默认禁止初始化期间覆盖设备/系统参数。</summary>
    public bool AllowConfigurationDownload { get; set; }
    /// <summary>默认只读核对当量；开启后才写入控制器。</summary>
    public bool ApplyAxisEquivalents { get; set; }
    /// <summary>空值保留控制器配置；必须核对接线后才指定。</summary>
    public bool? EmergencyInputInverted { get; set; }
    /// <summary>确认回零参数单位为轴单位/s、轴单位/s²后才启用 InUnit 回零接口。</summary>
    public bool HomingAxisUnitsPerSecondConfirmed { get; set; }
    /// <summary>确认所用卡/固件遵循本次C02普通插补协议后，才允许显式创建轨迹任务。</summary>
    public bool InterpolationProtocolConfirmed { get; set; }
    /// <summary>确认当前固件的原点输入为状态bit13；未确认时OriginKnown保持false。</summary>
    public bool OriginInputBitConfirmed { get; set; }
    internal InovanceNativeOptions Snapshot() => (InovanceNativeOptions)MemberwiseClone();
}

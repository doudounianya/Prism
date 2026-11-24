namespace Company.Hardware.ControlCard;

/// <summary>按功能描述能力，而不是按厂家名称把同一厂家的所有型号当成同一设备。</summary>
public enum MotionFeature
{
    SingleAxisMotion, Homing, DigitalInput, DigitalOutput, BufferedLine, PlanarArc,
    LookAhead, CoordinatePauseResume, PositionCompare, Pso, GantrySynchronization,
    Galvo, ContinuousGalvoGantry, ElectronicGear
}

/// <summary>未知必须保留，不能把尚未读取型号或未确认选件等同于已经支持。</summary>
public enum CapabilitySupport { Unknown, Supported, Unsupported }

/// <summary>离线检查只能验证协议和流程；真实时序、接线和工艺必须由实机验证。</summary>
public enum CapabilityValidation { None, Offline, Hardware }

/// <summary>
/// 不可变能力快照。硬件支持、驱动完成程度和当前运行条件分别表达。
/// 最大值是适用型号/协议的上限，不代替当前任务的轴、端口及单位校验。
/// </summary>
public sealed record MotionCapability(
    MotionFeature Feature,
    CapabilitySupport HardwareSupport,
    bool DriverImplemented,
    CapabilityValidation Validation,
    bool Ready,
    string Reason,
    int MaximumAxes = 0,
    int MaximumChannels = 0,
    bool IsSimulation = false)
{
    public bool CanExecute => HardwareSupport == CapabilitySupport.Supported && DriverImplemented && Ready;
}

/// <summary>身份与能力上限分开：当前映射轴数不是整张卡的型号上限，固件未知不得伪造。</summary>
public sealed record MotionControllerIdentity(
    string DeviceId, string Family, string Model, string LocalApiVersion,
    string FirmwareVersion, string FpgaVersion, bool? TerminalConnected,
    int MappedAxes, int ConfiguredInputPoints, int ConfiguredOutputPoints, bool IsSimulation = false);

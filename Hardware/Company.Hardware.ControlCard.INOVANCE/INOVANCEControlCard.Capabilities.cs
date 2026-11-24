namespace Company.Hardware.ControlCard.INOVANCE;

public sealed partial class INOVANCEControlCard
{
    /// <summary>
    /// 仅查询最近一次显式初始化保存的事实，不打开板卡，不读取无长度上限的原生版本字符串。
    /// GetHwSysPara的硬件类型是总线组合，不是032/016等具体型号；未知型号保留Unknown。
    /// </summary>
    protected override MotionControllerIdentity DescribeIdentity() => new(
        resourcesVerified ? $"IMC:{options.CardNumber}" : "IMC:Unknown", "IMC30G-E", "Unknown",
        apiVersion, systemVersion, fpgaVersion, resourcesVerified ? resources.terminalexist != 0 : null,
        resourcesVerified ? mappedAxes.Count : 0, InputStateBuffer.Count, OutputStateBuffer.Count);

    protected override IReadOnlyList<MotionCapability> DescribeCapabilities()
    {
        bool axisReady = resourcesVerified && AxisStateBuffer.All(x => x.Valid && x.Mapped && !x.Busy && !x.Paused &&
            !x.Homing && !x.Alarm && !x.EmergencyStop) && CoordinateStateBuffer.All(x => !x.Queued && !x.Enabled && !x.Paused);
        bool coordinateReady = axisReady && options.InterpolationProtocolConfirmed &&
            CoordinateStateBuffer.All(x => !x.Queued && !x.Enabled && !x.Paused);
        var basic = new[] { MotionFeature.SingleAxisMotion, MotionFeature.Homing,
            MotionFeature.DigitalInput, MotionFeature.DigitalOutput };
        var interpolation = new[] { MotionFeature.BufferedLine, MotionFeature.PlanarArc,
            MotionFeature.LookAhead, MotionFeature.CoordinatePauseResume };
        return Enum.GetValues<MotionFeature>().Select(feature =>
        {
            bool isBasic = basic.Contains(feature), isInterpolation = interpolation.Contains(feature);
            bool implemented = isBasic || isInterpolation;
            CapabilitySupport support = resourcesVerified && implemented ? CapabilitySupport.Supported : CapabilitySupport.Unknown;
            if (feature == MotionFeature.PositionCompare && resourcesVerified)
                support = resources.terminalexist != 0 ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
            bool ready = feature switch
            {
                MotionFeature.Homing => axisReady && options.HomingAxisUnitsPerSecondConfirmed,
                MotionFeature.DigitalInput or MotionFeature.DigitalOutput => resourcesVerified,
                _ => isInterpolation ? coordinateReady : isBasic && axisReady
            };
            string reason = feature == MotionFeature.PositionCompare && resourcesVerified
                ? resources.terminalexist != 0 ? "已发现端子板位置比较资源，但驱动未适配；维数和端口复用仍须单独核对。" : "未连接标准位置比较输出所需的端子板。"
                : !implemented ? "该高级功能的资源与协议尚未完成适配，不能按SDK函数名开放。"
                : !resourcesVerified ? "尚未核对当前板卡的物理轴绑定和资源。"
                : isInterpolation && !options.InterpolationProtocolConfirmed ? "插补已完成离线适配，现场配套协议尚未确认。"
                : feature == MotionFeature.Homing && !options.HomingAxisUnitsPerSecondConfirmed ? "回零参数单位和本机配置尚未确认。"
                : !ready ? "当前轴或任务状态尚不满足执行条件。"
                : "协议流程已离线验证；具体指令仍会重新读取反馈并检查参数，未代替现场验证。";
            return new MotionCapability(feature, support, implemented,
                implemented ? CapabilityValidation.Offline : CapabilityValidation.None, ready, reason,
                MaximumAxes: resourcesVerified && implemented ? mappedAxes.Count : 0,
                MaximumChannels: isInterpolation ? 4 : 0);
        }).ToArray();
    }
}

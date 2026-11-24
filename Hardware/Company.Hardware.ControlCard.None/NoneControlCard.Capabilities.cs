namespace Company.Hardware.ControlCard.None;

public sealed partial class NoneControlCard
{
    protected override MotionControllerIdentity DescribeIdentity() => new(
        "None", "Simulation", "ConstantVelocityModel", "Managed", "N/A", "N/A", null,
        _axes.Count, InputStateBuffer.Count, OutputStateBuffer.Count, IsSimulation: true);

    /// <summary>模拟支持必须带IsSimulation，不能拿离线流程通过来承诺真实同步精度。</summary>
    protected override IReadOnlyList<MotionCapability> DescribeCapabilities()
    {
        var implemented = new[] { MotionFeature.SingleAxisMotion, MotionFeature.Homing,
            MotionFeature.DigitalInput, MotionFeature.DigitalOutput, MotionFeature.BufferedLine,
            MotionFeature.CoordinatePauseResume, MotionFeature.ElectronicGear };
        return Enum.GetValues<MotionFeature>().Select(feature => new MotionCapability(feature,
            implemented.Contains(feature) ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
            implemented.Contains(feature), implemented.Contains(feature) ? CapabilityValidation.Offline : CapabilityValidation.None,
            _open && implemented.Contains(feature),
            implemented.Contains(feature) ? "离线状态流程模拟，不代表物理伺服或硬件时序。" : "此高级能力未在仿真器中实现。",
            MaximumAxes: _axes.Count, MaximumChannels: feature == MotionFeature.BufferedLine ? _coordinates.Count : 0, IsSimulation: true)).ToArray();
    }
}

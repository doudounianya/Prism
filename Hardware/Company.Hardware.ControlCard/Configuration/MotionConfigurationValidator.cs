namespace Company.Hardware.ControlCard;

/// <summary>设备文件、仿真和真实驱动共用的结构检查；不读取硬件，也不证明板卡具备所声明的能力。</summary>
public static class MotionConfigurationValidator
{
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    private static void Positive(double value, string name) => Require(double.IsFinite(value) && value > 0, name + "必须是有限正数。");
    public static void Validate(MotionConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Require(config.Axes is { Length: > 0 and <= 1024 } && config.Axes.All(a => a is not null && a.Axis.IsValid), "轴清单为空、超限或逻辑轴标识无效。");
        var axes = config.Axes!;
        Require(axes.Select(a => a.Axis).Distinct().Count() == axes.Length, "逻辑轴标识重复。");
        Require(axes.All(a => a.HardwareAxis is >= 0 and <= short.MaxValue) && axes.Select(a => a.HardwareAxis).Distinct().Count() == axes.Length, "物理轴号无效或重复；同一卡的总线与本地轴共用控制器轴号。");
        Require(config.Coordinates is { Length: <= 256 } && config.Coordinates.All(c => c is not null && c.CoordinateId.IsValid), "插补组清单或标识无效。");
        var groups = config.Coordinates!;
        Require(groups.Select(c => c.CoordinateId).Distinct().Count() == groups.Length, "逻辑插补组标识重复。");
        Require(groups.All(c => c.HardwareCoordinate is >= 0 and <= short.MaxValue) && groups.Select(c => c.HardwareCoordinate).Distinct().Count() == groups.Length, "控制器坐标系槽号无效或重复。");
        var configured = axes.Select(a => a.Axis).ToHashSet();
        Require(config.CommandTimeoutMs is > 0 and <= 300000 && config.PollIntervalMs is > 0 and <= 1000, "等待超时或轮询间隔超出范围。");
        Require(config.InputBankCount <= 4096 && config.OutputBankCount <= 4096, "IO节点数量超过软件资源上限。");
        foreach (var axis in axes)
        {
            Require(Enum.IsDefined(axis.Bus) && Enum.IsDefined(axis.Kind) && Enum.IsDefined(axis.Unit), "轴总线、类型或单位无效。");
            Require(axis.Kind == MotionAxisKind.Linear ? axis.Unit == MotionAxisUnit.Millimetre : axis.Unit == MotionAxisUnit.Degree, "直线轴使用毫米，旋转轴使用角度；不得隐式混用单位。");
            Positive(axis.PulsesPerUnit,"脉冲当量"); Positive(axis.PositioningVelocity,"定位速度"); Positive(axis.PositioningAcceleration,"定位加速度"); Positive(axis.PositioningDeceleration,"定位减速度");
            Positive(axis.JogVelocity,"点动速度"); Positive(axis.JogAcceleration,"点动加速度"); Positive(axis.JogDeceleration,"点动减速度");
            Require(axis.HomingSearchVelocity > 0 && axis.HomingApproachVelocity > 0 && axis.HomingAcceleration > 0 && axis.HomingTimeoutMs > 0,"回零参数必须大于零。");
            Require(double.IsFinite(axis.DefaultPosition),"默认位置必须有限。");
            Require(double.IsFinite(axis.SoftLimitNegative) && double.IsFinite(axis.SoftLimitPositive) && axis.SoftLimitNegative < axis.SoftLimitPositive,"软件限位上下界无效。");
        }
        foreach (var group in groups)
        {
            Require(group.Axes is { Length: 3 } && group.Axes.All(configured.Contains) && group.Axes.Distinct().Count() == 3,"当前插补组必须引用三个不同的已配置轴；数组顺序对应轨迹三个分量。");
            Positive(group.LinearVelocity,"插补速度"); Positive(group.LinearAcceleration,"插补加速度"); Positive(group.LinearDeceleration,"插补减速度");
            Positive(group.ArcVelocity,"圆弧速度"); Positive(group.ArcAcceleration,"圆弧加速度"); Positive(group.TransitionTolerance,"过渡精度"); Positive(group.ArrivalTolerance,"到位容差");
            Require(group.LookAheadSegments is >= 0 and <= 10000,"前瞻段数超出范围。");
        }
        Require(config.Synchronizations is { Length: <= 1024 } && config.Synchronizations.All(s => s is not null && MotionIdentifier.IsValid(s.Id)),"同步关系清单或标识无效。");
        var synchronization = config.Synchronizations!;
        Require(synchronization.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == synchronization.Length,"同步关系标识重复。");
        foreach (var relation in synchronization)
        {
            Require(configured.Contains(relation.MasterAxis) && configured.Contains(relation.SlaveAxis) && relation.MasterAxis != relation.SlaveAxis,"同步主从轴必须存在且互不相同。");
            Require(double.IsFinite(relation.Ratio) && relation.Ratio != 0,"同步比例必须是有限非零数；负数表示反向跟随。");
            Require(Enum.IsDefined(relation.Mode) && Enum.IsDefined(relation.MasterSource),"同步模式或主轴命令源无效。");
            Require(axes.Single(a => a.Axis == relation.MasterAxis).Unit == axes.Single(a => a.Axis == relation.SlaveAxis).Unit,"同步主从轴单位必须一致。");
        }
        var enabled = synchronization.Where(s => s.Enabled).ToArray();
        Require(enabled.Select(s => s.SlaveAxis).Distinct().Count() == enabled.Length,"一个从轴不能同时跟随多个主轴。");
        var slaves = enabled.Select(s => s.SlaveAxis).ToHashSet();
        Require(enabled.All(s => !slaves.Contains(s.MasterAxis)),"同步关系存在环路或级联；当前仅支持一级主从关系。");
        Require(groups.Where(c => c.CoordinateEnabled).All(c => c.Axes.All(a => !slaves.Contains(a))),"同步从轴不能再作为独立插补维度。");
    }
}

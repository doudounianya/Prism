namespace Company.Hardware.ControlCard.AdTech;

/// <summary>
/// 真实板卡接入前需按厂商定义核实的协议选项；默认空值不允许推断保护输入正常。
/// 这不是设备急停/限位配置，不能用来覆盖硬接线；每次显式初始化取快照，运行中不读取公开可变对象。
/// </summary>
public sealed class AdtechNativeOptions
{
    private readonly object sync = new();
    private bool? protectionInputValuesAreRawLevels;
    private bool allowBusRestart;
    private bool applyAxisConfiguration;
    private bool applySoftLimits;

    /// <summary>允许初始化时停止总线、加载卡内配置并重新启动。默认不授权，不猜测未说明的主站状态枚举。</summary>
    public bool AllowBusRestart { get { lock (sync) return allowBusRestart; } set { lock (sync) allowBusRestart = value; } }
    /// <summary>允许向停用且静止的轴写编程单位及当量；默认只读核对已有卡配置。</summary>
    public bool ApplyAxisConfiguration { get { lock (sync) return applyAxisConfiguration; } set { lock (sync) applyAxisConfiguration = value; } }
    /// <summary>允许按本次配置写入软限位，包含显式禁用；默认保留并读取卡上已有保护。</summary>
    public bool ApplySoftLimits { get { lock (sync) return applySoftLimits; } set { lock (sync) applySoftLimits = value; } }
    /// <summary>
    /// true：已确认 SDK 输入读数是原始高低电平，需与卡内有效电平比较；
    /// false：已确认 SDK 已归一化为 1=触发，不再二次取反；null：未确认，拒绝真实初始化。
    /// </summary>
    public bool? ProtectionInputValuesAreRawLevels
    {
        get { lock (sync) return protectionInputValuesAreRawLevels; }
        set { lock (sync) protectionInputValuesAreRawLevels = value; }
    }

    /// <summary>一次会话使用独立副本；运行中编辑公开选项只在关闭并重新初始化后生效。</summary>
    internal AdtechNativeOptions Snapshot()
    {
        lock (sync) return new AdtechNativeOptions
        {
            ProtectionInputValuesAreRawLevels = protectionInputValuesAreRawLevels,
            AllowBusRestart = allowBusRestart,
            ApplyAxisConfiguration = applyAxisConfiguration,
            ApplySoftLimits = applySoftLimits
        };
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Company.Hardware.ControlCard;

#region 整卡配置

/// <summary>
/// 一次运动控制会话的配置。Initialize 会复制本对象及轴、坐标系数组，之后修改原对象不会改变当前会话。
/// </summary>
public class MotionConfiguration
{
    public MotionConfiguration()
    {
        // 保留旧四轴默认值供既有离线调用使用；设备文件可提供任意有效轴清单。
        Axes = new[] { AxisId.X, AxisId.Y, AxisId.Z, AxisId.U }.Select((axis,index) => new AxisConfiguration { Axis=axis, HardwareAxis=index }).ToArray();
        Coordinates = Enumerable.Range(0,4).Select(index => new CoordinateConfiguration { CoordinateId=new("Coordinate"+index), HardwareCoordinate=index }).ToArray();
    }
    public SynchronizationConfiguration[] Synchronizations { get; set; } = [];
    public void Validate() => MotionConfigurationValidator.Validate(this);
    /// <summary>
    /// 单轴参数
    /// </summary>
    public AxisConfiguration[] Axes { get; set; }


    /// <summary>
    /// XYZ 3轴插补参数
    /// </summary>
    public CoordinateConfiguration[] Coordinates { get; set; }


    /// <summary>
    /// 驱动参数文件
    /// </summary>
    public string DeviceParameterFile { get; set; } = string.Empty;


    /// <summary>
    /// 系统参数文件
    /// </summary>
    public string SystemParameterFile { get; set; } = string.Empty;


    /// <summary>输入地址组数，每组 16 位；实际硬件资源仍由驱动核对，不表示控制器型号的 IO 总上限。</summary>
    public uint InputBankCount { get; set; } = 10;
    /// <summary>输出地址组数，点位地址由 DigitalPointState.Bank 与 Bit 共同确定。</summary>
    public uint OutputBankCount { get; set; } = 10;

    /// <summary>必须显式启用才允许初始化；加载程序集和注册服务不会连接设备。</summary>
    public bool Enabled { get; set; }

    /// <summary>真实驱动的第二道启用条件。默认关闭，None 仿真不需要此项。</summary>
    public bool AllowNativeHardware { get; set; }

    /// <summary>有界状态等待的默认时限，单位毫秒。</summary>
    public int CommandTimeoutMs { get; set; } = 10000;

    /// <summary>等待动作时的轮询间隔，单位毫秒；不改变现有 PLC 采样设置。</summary>
    public int PollIntervalMs { get; set; } = 20;

    /// <summary>建立当前会话的独立配置；修改原配置须关闭后重新初始化，不能影响运行中命令。</summary>
    public MotionConfiguration Snapshot()
    {
        var copy = (MotionConfiguration)MemberwiseClone();
        copy.Axes = Axes?.Select(x => x?.Snapshot()!).ToArray()!;
        copy.Coordinates = Coordinates?.Select(x => x?.Snapshot()!).ToArray()!;
        copy.Synchronizations = Synchronizations?.Select(x => x?.Snapshot()!).ToArray()!;
        return copy;
    }

}

#endregion

#region 单轴配置

public class AxisConfiguration
{
    /// <summary>
    /// 轴类型
    /// </summary>
    public AxisId Axis { get; set; }
    public string Name { get; set; } = string.Empty;
    public MotionBusKind Bus { get; set; }
    public MotionAxisKind Kind { get; set; }
    public MotionAxisUnit Unit { get; set; } = MotionAxisUnit.Millimetre;

    /// <summary>零基物理轴索引；驱动负责适配厂商轴号，汇川直接使用此值。</summary>
    public int HardwareAxis { get; set; }

    /// <summary>是否启用软件行程校验。真实控制器中的硬限位保护独立保留。</summary>
    public bool SoftLimitEnabled { get; set; }
    public double SoftLimitNegative { get; set; } = -1000000;
    public double SoftLimitPositive { get; set; } = 1000000;

    /// <summary>冻结一份单轴参数，防止外部同时修改本次运动参数。</summary>
    public AxisConfiguration Snapshot() => (AxisConfiguration)MemberwiseClone();

    /// <summary>
    /// 脉冲当量
    /// </summary>
    public double PulsesPerUnit { get; set; } = 22500;


    /// <summary>
    /// 回原点方式
    /// </summary>
    public Int16 HomingMethod { get; set; } = 17;


    /// <summary>
    /// 回原点高速
    /// </summary>
    public UInt32 HomingSearchVelocity { get; set; } = 100;


    /// <summary>
    /// 回原点低速
    /// </summary>
    public UInt32 HomingApproachVelocity { get; set; } = 10;


    /// <summary>
    /// 回原点加速度
    /// </summary>
    public UInt32 HomingAcceleration { get; set; } = 1000;


    /// <summary>
    /// 回原点超时时间
    /// </summary>
    public UInt32 HomingTimeoutMs { get; set; } = 10000;


    /// <summary>
    /// PTP位置
    /// </summary>
    public Double DefaultPosition { get; set; } = 0;


    /// <summary>
    /// PTP速度
    /// </summary>
    public Double PositioningVelocity { get; set; } = 100;


    /// <summary>
    /// PTP加速度
    /// </summary>
    public Double PositioningAcceleration { get; set; } = 1000;


    /// <summary>
    /// PTP减速度
    /// </summary>
    public Double PositioningDeceleration { get; set; } = 1000;


    /// <summary>
    /// Jog速度
    /// </summary>
    public Double JogVelocity { get; set; } = 100;


    /// <summary>
    /// Jog加速度
    /// </summary>
    public Double JogAcceleration { get; set; } = 1000;


    /// <summary>
    /// Jog减速度
    /// </summary>
    public Double JogDeceleration { get; set; } = 1000;
}

#endregion

#region 三轴坐标系配置

public class CoordinateConfiguration
{
    /// <summary>
    /// 坐标系
    /// </summary>
    public CoordinateId CoordinateId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>控制器坐标系槽号，与逻辑组名称无关；具体驱动另行检查可用范围。</summary>
    public int HardwareCoordinate { get; set; }
    /// <summary>三个轨迹分量按此顺序绑定逻辑轴，可将第三分量绑定旋转轴 R1。</summary>
    public AxisId[] Axes { get; set; } = [AxisId.X, AxisId.Y, AxisId.Z];

    /// <summary>坐标参数独立快照；三个逻辑轴必须唯一且属于当前配置。</summary>
    public CoordinateConfiguration Snapshot()
    {
        var copy = (CoordinateConfiguration)MemberwiseClone(); copy.Axes = Axes?.ToArray()!; return copy;
    }

    /// <summary>
    /// 轴类型
    /// </summary>
    // 原代码的分量访问兼容；JSON只保存有序轴数组，避免两份映射互相覆盖。
    [System.Text.Json.Serialization.JsonIgnore] public AxisId XAxis { get => Axes[0]; set => Axes[0]=value; }
    [System.Text.Json.Serialization.JsonIgnore] public AxisId YAxis { get => Axes[1]; set => Axes[1]=value; }
    [System.Text.Json.Serialization.JsonIgnore] public AxisId ZAxis { get => Axes[2]; set => Axes[2]=value; }


    /// <summary>
    /// XYZ直线插补速度
    /// </summary>
    public double LinearVelocity { get; set; } = 100;


    /// <summary>
    /// XYZ直线插补加速度
    /// </summary>
    public double LinearAcceleration { get; set; } = 1000;
    /// <summary>轨迹减速度，和加速度独立配置，单位为轴单位/s²。</summary>
    public double LinearDeceleration { get; set; } = 1000;
    /// <summary>PC前瞻段数；只改变控制卡轨迹缓冲，不改变PLC或HMI采集周期。</summary>
    public int LookAheadSegments { get; set; } = 256;
    /// <summary>轨迹过渡精度，单位取决于已确认的轴当量，必须显式有限正数。</summary>
    public double TransitionTolerance { get; set; } = 0.01;
    /// <summary>最终位置及暂停恢复位置核对容差，不能拿轨迹过渡精度代替机械到位确认。</summary>
    public double ArrivalTolerance { get; set; } = 0.000001;
    /// <summary>允许应用显式创建并使用此逻辑坐标系；不会覆盖其他应用已有坐标系。</summary>
    public bool CoordinateEnabled { get; set; } = true;




    /// <summary>
    /// 平面圆弧插补速度；本接口使用圆心偏移，未开放整圆、多圈及螺旋。
    /// </summary>
    public double ArcVelocity { get; set; } = 100;


    /// <summary>
    /// 平面圆弧插补加速度，按已经确认的轴单位配置。
    /// </summary>
    public double ArcAcceleration { get; set; } = 1000;
}

#endregion

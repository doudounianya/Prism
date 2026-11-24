using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Company.Hardware.ControlCard;

#region 停止方式

/// <summary>
/// 轴停止模式
/// </summary>
public enum StopMode
{
    Decelerate,
    Immediate,
}

#endregion

#region 运动方向

/// <summary>
/// 运动轴在连续运动的方向
/// </summary>
public enum JogDirection
{
    Positive,
    Negative,
}

#endregion

#region 轴反馈状态

public class AxisState
{
    /// <summary>
    /// 轴类型
    /// </summary>
    public AxisId Axis { get; set; }

    /// <summary>只有本会话实际读取成功才有效；旧连接或读取失败不能用于动作准入。</summary>
    public bool Valid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int RawStatus { get; set; }
    public bool Homed { get; set; }
    public bool Arrived { get; set; }
    public bool Paused { get; set; }
    public bool PositiveLimit { get; set; }
    public bool NegativeLimit { get; set; }
    public bool Origin { get; set; }
    /// <summary>原点位的协议/固件含义已确认。未知时Origin=false不能解释成传感器未触发。</summary>
    public bool OriginKnown { get; set; }
    public bool EmergencyStop { get; set; }
    /// <summary>物理映射是否已经确认。仿真和既有驱动默认兼容；具体真实驱动应主动核对。</summary>
    public bool Mapped { get; set; } = true;
    /// <summary>独立的回零生命周期；不能把原点输入、普通到位或轴空闲当成回零成功。</summary>
    public bool Homing { get; set; }
    /// <summary>警告单独保留，不自动把所有警告解释为已经解除的故障。</summary>
    public bool Warning { get; set; }
    public int FaultCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>界面只能得到副本；更改副本不会篡改驱动内的动作判断。</summary>
    public AxisState Snapshot() => (AxisState)MemberwiseClone();
    /// <summary>
    /// 使能
    /// </summary>
    public bool Enable { get; set; }

    /// <summary>
    /// 忙碌
    /// </summary>
    public bool Busy { get; set; }

    /// <summary>
    /// 报警
    /// </summary>
    public bool Alarm { get; set; }

    /// <summary>
    /// 规划位置
    /// </summary>
    public Double CommandPosition { get; set; }

    /// <summary>
    /// 规划速度
    /// </summary>
    public Double CommandVelocity { get; set; }

    /// <summary>
    /// 规划加速度
    /// </summary>
    public Double CommandAcceleration { get; set; }

    /// <summary>
    /// 反馈位置
    /// </summary>
    public Double ActualPosition { get; set; }

    /// <summary>
    /// 反馈速度
    /// </summary>
    public Double ActualVelocity { get; set; }

    /// <summary>
    /// 反馈加速度
    /// </summary>
    public Double ActualAcceleration { get; set; }

}

#endregion

#region 坐标系反馈状态

public class CoordinateState
{
    /// <summary>
    /// 坐标系是否存在
    /// </summary>
    public CoordinateId CoordinateId { get; set; }
    public bool Valid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int RawStatus { get; set; }
    public bool Paused { get; set; }
    /// <summary>最后一段到位反馈；搬运完成或队列暂时为空均不能代替此值。</summary>
    public bool Arrived { get; set; }
    /// <summary>本会话仍持有插补持续使能，包含队列暂时为空的时间。</summary>
    public bool Enabled { get; set; }
    /// <summary>本任务仍有数据/清理责任，不能仅凭原生 Busy=false 释放轴。</summary>
    public bool Queued { get; set; }
    /// <summary>已确认停止并清除本任务队列；取消不表示到达原目标。</summary>
    public bool Cleared { get; set; }
    public int FaultCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>向调用者返回状态副本，避免外部修改本次运动上下文。</summary>
    public CoordinateState Snapshot() => (CoordinateState)MemberwiseClone();
    /// <summary>
    /// 坐标系是否存在
    /// </summary>
    public bool Exist { get; set; }
    /// <summary>
    /// 坐标系是否运行中
    /// </summary>
    public bool Busy { get; set; }
    /// <summary>
    /// 坐标系是否报警
    /// </summary>
    public bool Alarm { get; set; }
}

#endregion

#region 数字输入输出状态

/// <summary>
/// 控制卡输入输出IO点位，用于读取和写入
/// </summary>
public class DigitalPointState
{
    /// <summary>
    /// IO 地址组编号，每组 16 位；不等同于控制卡实例编号。
    /// </summary>
    public uint Bank { get; }

    /// <summary>
    /// 当前组内的位编号，范围为 [0,15]。
    /// </summary>
    public int Bit { get; }


    /// <summary>
    /// 值，[false, true]
    /// </summary>
    public bool Value { get; set; } // 允许内部设置
    public bool Valid { get; set; }
    /// <summary>厂商节点可能少于16点；不存在的端口应单独标记，不显示成有效OFF。</summary>
    public bool Available { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>IO 显示快照，不允许通过界面副本直接修改驱动状态。</summary>
    public DigitalPointState Snapshot() => new(Bank, Bit) { Value = Value, Valid = Valid, Available = Available, UpdatedAt = UpdatedAt };



    public DigitalPointState(uint board, int port)
    {
        Bank = board;
        Bit = port;
        Value = false;
    }
}

#endregion

#region 三维直线路径点

public class Position3D
{
    // 一个对象是一处三维轨迹点，支持小数；点列表长度与“三根轴”是不同概念。
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

#endregion

#region 不可变轨迹段

public enum TrajectorySegmentKind { Line, ArcXY, ArcYZ, ArcZX }

/// <summary>
/// 绝对终点轨迹段，所有位置使用已确认的轴单位。
/// 圆弧中心始终是相对于本段起点的平面偏移：XY为X/Y，YZ为Y/Z，ZX为Z/X。
/// 本批只开放平面圆弧，不用未声明的高度或圈数隐式产生螺旋和整圆。
/// </summary>
public sealed record TrajectorySegment(
    TrajectorySegmentKind Kind, double X, double Y, double Z,
    double CenterU = 0, double CenterV = 0, bool Clockwise = true, int UserId = 0);

#endregion

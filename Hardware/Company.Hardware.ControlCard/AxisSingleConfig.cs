using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    public class AxisSingleConfig
    {
        /// <summary>
        /// 轴类型
        /// </summary>
        public AxisType Axis { get; set; }

        /// <summary>零基物理轴索引；驱动负责转换厂商轴号，AdTech 使用此值加一，汇川直接使用此值。</summary>
        public int HardwareAxis { get; set; }

        /// <summary>是否启用软件行程校验。真实控制器中的硬限位保护独立保留。</summary>
        public bool SoftLimitEnabled { get; set; }
        public double SoftLimitNegative { get; set; } = -1000000;
        public double SoftLimitPositive { get; set; } = 1000000;

        /// <summary>冻结一份单轴参数，防止外部同时修改本次运动参数。</summary>
        public AxisSingleConfig Snapshot() => (AxisSingleConfig)MemberwiseClone();

        /// <summary>
        /// 脉冲当量
        /// </summary>
        public double Equiv { get; set; } = 22500;


        /// <summary>
        /// 回原点方式
        /// </summary>
        public Int16 HomeMethod { get; set; } = 17;


        /// <summary>
        /// 回原点高速
        /// </summary>
        public UInt32 HomehighVel { get; set; } = 100;


        /// <summary>
        /// 回原点低速
        /// </summary>
        public UInt32 HomelowVel { get; set; } = 10;


        /// <summary>
        /// 回原点加速度
        /// </summary>
        public UInt32 HomeAcc { get; set; } = 1000;


        /// <summary>
        /// 回原点超时时间
        /// </summary>
        public UInt32 HomeOvertime { get; set; } = 10000;


        /// <summary>
        /// PTP位置
        /// </summary>
        public Double PtpPos { get; set; } = 0;


        /// <summary>
        /// PTP速度
        /// </summary>
        public Double PtpVel { get; set; } = 100;


        /// <summary>
        /// PTP加速度
        /// </summary>
        public Double PtpAcc { get; set; } = 1000;


        /// <summary>
        /// PTP减速度
        /// </summary>
        public Double PtpDec { get; set; } = 1000;


        /// <summary>
        /// Jog速度
        /// </summary>
        public Double JogVel { get; set; } = 100;


        /// <summary>
        /// Jog加速度
        /// </summary>
        public Double JogAcc { get; set; } = 1000;


        /// <summary>
        /// Jog减速度
        /// </summary>
        public Double JogDec { get; set; } = 1000;
    }
}

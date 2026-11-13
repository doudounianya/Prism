using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    public class AxisStatus
    {
        /// <summary>
        /// 轴类型
        /// </summary>
        public AxisType Axis { get; set; }

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
        public bool EmergencyStop { get; set; }

        /// <summary>界面只能得到副本；更改副本不会篡改驱动内的动作判断。</summary>
        public AxisStatus Snapshot() => (AxisStatus)MemberwiseClone();
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
        public Double PrfPos { get; set; }

        /// <summary>
        /// 规划速度
        /// </summary>
        public Double PrfVel { get; set; }

        /// <summary>
        /// 规划加速度
        /// </summary>
        public Double PrfAcc { get; set; }

        /// <summary>
        /// 反馈位置
        /// </summary>
        public Double EncfPos { get; set; }

        /// <summary>
        /// 反馈速度
        /// </summary>
        public Double EncVel { get; set; }

        /// <summary>
        /// 反馈加速度
        /// </summary>
        public Double EncAcc { get; set; }

    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    public class CrdStatus
    {
        /// <summary>
        /// 坐标系是否存在
        /// </summary>
        public CrdType CrdType { get; set; }
        public bool Valid { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int RawStatus { get; set; }
        public bool Paused { get; set; }
        /// <summary>向调用者返回状态副本，避免外部修改本次运动上下文。</summary>
        public CrdStatus Snapshot() => (CrdStatus)MemberwiseClone();
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
}

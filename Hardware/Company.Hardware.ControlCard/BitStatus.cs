using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    /// <summary>
    /// 控制卡输入输出IO点位，用于读取和写入
    /// </summary>
    public class BitStatus
    {
        /// <summary>
        /// 节点号
        /// </summary>
        public uint Board { get; }

        /// <summary>
        /// 端口号，[0,15]
        /// </summary>
        public int Port { get; }


        /// <summary>
        /// 值，[false, true]
        /// </summary>
        public bool Value { get; set; } // 允许内部设置
        public bool Valid { get; set; }
        /// <summary>厂商节点可能少于16点；不存在的端口应单独标记，不显示成有效OFF。</summary>
        public bool Available { get; set; } = true;
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>IO 显示快照，不允许通过界面副本直接修改驱动状态。</summary>
        public BitStatus Snapshot() => new(Board, Port) { Value = Value, Valid = Valid, Available = Available, UpdatedAt = UpdatedAt };



        public BitStatus(uint board, int port)
        {
            Board = board;
            Port = port;
            Value = false;
        }
    }
}

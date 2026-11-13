using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    public class PosXYZ
    {
        // 一个对象是一处三维轨迹点，支持小数；点列表长度与“三根轴”是不同概念。
        public double Pos_X { get; set; }
        public double Pos_Y { get; set; }
        public double Pos_Z { get; set; }
    }
}

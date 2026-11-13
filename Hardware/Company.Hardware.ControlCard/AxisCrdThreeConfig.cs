using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    public class AxisThreeConfig
    {
        /// <summary>
        /// 坐标系
        /// </summary>
        public CrdType CrdType { get; set; }

        /// <summary>坐标参数独立快照；三个逻辑轴必须唯一且属于当前配置。</summary>
        public AxisThreeConfig Snapshot() => (AxisThreeConfig)MemberwiseClone();

        /// <summary>
        /// 轴类型
        /// </summary>
        public AxisType Axis_X { get; set; }
        public AxisType Axis_Y { get; set; }
        public AxisType Axis_Z { get; set; }


        /// <summary>
        /// XYZ直线插补速度
        /// </summary>
        public double CrdLineVel { get; set; } = 100;


        /// <summary>
        /// XYZ直线插补加速度
        /// </summary>
        public double CrdLineAcc { get; set; } = 1000;




        /// <summary>
        /// XYZ三点圆弧插补速度
        /// </summary>
        public double CrdArcVel { get; set; } = 100;


        /// <summary>
        /// XYZ三点圆弧插补加速度
        /// </summary>
        public double CrdArcAcc { get; set; } = 1000;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Hardware.ControlCard
{
    /// <summary>
    /// 控制卡
    /// </summary>
    public class ControlCardConfig
    {
        public ControlCardConfig() {
            int count = Enum.GetValues(typeof(AxisType)).Length;
            AxisSingleConfigs = new AxisSingleConfig[count];
            int index = 0;
            foreach (var item in Enum.GetValues(typeof(AxisType)))
            {
                AxisSingleConfigs[index] = new AxisSingleConfig()
                {
                    Axis = (AxisType)item,
                    HardwareAxis = (int)item
                };
                index++;
            }


            count = Enum.GetValues(typeof(CrdType)).Length;
            AxisThreeConfigs = new AxisThreeConfig[count];
            index = 0;
            foreach (var item in Enum.GetValues(typeof(CrdType)))
            {
                AxisThreeConfigs[index] = new AxisThreeConfig()
                {
                    CrdType = (CrdType)item,
                    Axis_X = AxisType.X, Axis_Y = AxisType.Y, Axis_Z = AxisType.Z,
                };
                index++;
            }
        }
        /// <summary>
        /// 单轴参数
        /// </summary>
        public AxisSingleConfig[] AxisSingleConfigs { get; set; }


        /// <summary>
        /// XYZ 3轴插补参数
        /// </summary>
        public AxisThreeConfig[] AxisThreeConfigs { get; set; }


        /// <summary>
        /// 驱动参数文件
        /// </summary>
        public string DeviceFile { get; set; } = string.Empty;


        /// <summary>
        /// 系统参数文件
        /// </summary>
        public string SystemFile { get; set; } = string.Empty;


        public uint DiNum { get; set; } = 10;
        public uint DoNum { get; set; } = 10;

        /// <summary>必须显式启用才允许初始化；加载程序集和注册服务不会连接设备。</summary>
        public bool Enabled { get; set; }

        /// <summary>真实驱动的第二道启用条件。默认关闭，None 仿真不需要此项。</summary>
        public bool AllowNativeHardware { get; set; }

        /// <summary>有界状态等待的默认时限，单位毫秒。</summary>
        public int CommandTimeoutMs { get; set; } = 10000;

        /// <summary>等待动作时的轮询间隔，单位毫秒；不改变现有 PLC 采样设置。</summary>
        public int PollIntervalMs { get; set; } = 20;

        /// <summary>建立当前会话的独立配置；修改原配置须关闭后重新初始化，不能影响运行中命令。</summary>
        public ControlCardConfig Snapshot()
        {
            var copy = (ControlCardConfig)MemberwiseClone();
            copy.AxisSingleConfigs = AxisSingleConfigs?.Select(x => x?.Snapshot()!).ToArray()!;
            copy.AxisThreeConfigs = AxisThreeConfigs?.Select(x => x?.Snapshot()!).ToArray()!;
            return copy;
        }

    }
}

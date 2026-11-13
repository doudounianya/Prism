using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Application.Share.Config
{
    /// <summary>供配置模块与界面共享的软件参数；具体加载、保存和通知由配置提供者实现。</summary>
    public class SoftwareConfig
    {
        /// <summary>软件显示名称；未加载自定义值时使用应用默认名称。</summary>
        public string Name { get; set; } = "CRT HMI";
    }
}

using Prism.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Platform.Shared.Events
{
    /// <summary>
    /// 关闭所有硬件
    /// </summary>
    /// <remarks>不带载荷的 Prism 全局关闭通知；各硬件模块自行订阅并执行自身清理，事件不负责资源所有权。</remarks>
    public class CloseAllHardwareEvent : PubSubEvent
    {
    }
}

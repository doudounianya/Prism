using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Platform.Shared.Config
{
    /// <summary>
    /// 软件配置提供者
    /// </summary>
    /// <remarks>跨模块只依赖此配置契约；OnChanged 用于通知消费者重新读取 SoftwareConfig，事件本身不携带增量。</remarks>
    public interface ISoftwareConfigProvider
    {
        /// <summary>
        /// 软件参数
        /// </summary>
        SoftwareConfig SoftwareConfig { get; }

        /// <summary>
        /// 参数改变事件
        /// </summary>
        event Action OnChanged;
    }
}

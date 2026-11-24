using Company.Platform.Shared.Config;

namespace Company.PlcHmi.Configuration.Models
{
    /// <summary>
    /// 本地系统配置的序列化根对象，目前聚合软件显示与行为配置。
    /// 新建根对象即提供 SoftwareConfig 默认实例，供缺少已有配置时使用。
    /// </summary>
    public class SystemConfigModel
    {
        /// <summary>由配置管理器持久化、由软件配置提供器向应用公开的软件配置对象。</summary>
        public SoftwareConfig SoftwareConfig { get; set; } = new SoftwareConfig();
    }
}

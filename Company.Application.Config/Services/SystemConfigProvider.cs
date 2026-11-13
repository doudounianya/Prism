using Company.Application.Share.Config;
using Company.Core.Ioc;

namespace Company.Application.Config.Services
{
    /// <summary>
    /// 向应用公开软件配置及变更事件，构造时引用管理器当前根对象中的 SoftwareConfig。
    /// 提供器本身不持久化；配置弹窗保存成功后显式调用 Invoke 通知订阅方刷新。
    /// </summary>
    [ExposedService(Lifetime.Singleton, typeof(ISystemConfigProvider), typeof(ISoftwareConfigProvider))]
    public class SystemConfigProvider : ISystemConfigProvider
    {
        /// <summary>取得当前软件配置引用，不克隆配置，也不在构造函数中发布变更。</summary>
        public SystemConfigProvider(SystemConfigManager systemConfigManager)
        {
            SoftwareConfig = systemConfigManager.Config.SoftwareConfig;
        }

        // 同步调用订阅者；此处不切换线程，也不重新读取管理器的 Config。
        internal void Invoke()
        {
            OnChanged?.Invoke();
        }

        /// <summary>当前对外公开的软件配置；设置引用本身不会自动触发 OnChanged。</summary>
        public SoftwareConfig SoftwareConfig { get; set; }
        /// <summary>由保存入口显式发布的软件配置变更通知。</summary>
        public event Action OnChanged;
    }
}

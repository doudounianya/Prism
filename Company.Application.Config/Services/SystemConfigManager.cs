using Company.Application.Config.Models;
using Company.Core.Config;
using Company.Core.Ioc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Application.Config.Services
{
    /// <summary>
    /// 通过 IConfigManager 读写本地系统配置根对象，构造时执行首次加载。
    /// 只管理内存对象与持久化调用，不包含界面权限判断或配置变更通知。
    /// </summary>
    [ExposedService(Lifetime.Singleton)]
    public class SystemConfigManager
    {
        public SystemConfigModel Config { get; set; }

        private IConfigManager ConfigManager { get; }
        /// <summary>保存持久化服务依赖，并立即读取当前系统配置。</summary>
        public SystemConfigManager(IConfigManager configManager)
        {
            ConfigManager = configManager;

            Load();
        }

        /// <summary>
        /// 按系统配置键读取根对象；读取结果为空时建立默认配置。
        /// 底层读取异常由调用方处理，此处不将异常转换成默认值。
        /// </summary>
        public void Load()
        {
            // 仅 null 结果触发默认对象；Load 会替换 Config 引用，既有外部引用不会自动重新绑定。
            Config = ConfigManager.Read<SystemConfigModel>(ConfigKey.SystemConfig) ?? new SystemConfigModel(); ;
        }

        /// <summary>
        /// 把当前内存中的完整 Config 对象写入系统配置键；成功返回后才由调用方通知界面。
        /// </summary>
        public void Save()
        {
            ConfigManager.Write(ConfigKey.SystemConfig, Config);
        }

    }
}

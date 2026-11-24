using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prism.Ioc;
using Prism.Modularity;
using System.Text;
using System.Threading.Tasks;
using Company.Core.Extension;

namespace Company.Core
{
    public class CoreModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 基础库不再依赖 Prism。桌面装配层负责注册两个程序集，
            // 保持配置、缓存及语言服务原有的单例身份和延迟创建方式。
            containerRegistry.RegisterAssembly(typeof(Config.IConfigManager).Assembly);
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(typeof(Config.IConfigManager).Assembly);
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
        }
    }
}

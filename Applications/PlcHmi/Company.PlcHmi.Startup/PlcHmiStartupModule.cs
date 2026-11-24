using Company.PlcHmi.Startup.Views;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Extension;
using Prism.Ioc;
using Prism.Modularity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.PlcHmi.Startup
{
    /// <summary>
    /// 启动准备页的模块入口，依赖应用配置及 PLC 模块，确保准备页解析前其服务已注册。
    /// 此模块本身只建立服务和导航注册；运行时启动由 InitializeViewModel 的加载命令完成。
    /// </summary>
    [Module(ModuleName = ModuleNames.PlcHmiStartupModule, OnDemand = true)]
    [ModuleDependency(ModuleNames.PlcHmiConfigurationModule)] //进入主界面前加载应用配置
    [ModuleDependency(ModuleNames.PlcHmiPlcModule)]
    [ModuleDependency("MotionControllerModule")]
    public class PlcHmiStartupModule : IModule
    {
        /// <summary>扫描程序集并将 InitializeView 注册为可导航的启动准备页。</summary>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());

            containerRegistry.RegisterForNavigation<InitializeView>();
        }

        /// <summary>执行当前程序集声明的初始化行为，不在此回调中显式启动 PLC。</summary>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
        }
    }
}

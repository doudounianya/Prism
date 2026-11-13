using Company.Application.Config.Views;
using Company.Application.Share.Prism;
using Company.Core.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.Application.Config
{
    /// <summary>
    /// 应用配置模块的 Prism 入口，注册程序集内配置服务及系统配置弹窗。
    /// 注册阶段不主动读取 PLC 绑定文件，绑定配置通过独立提供器显式加载。
    /// </summary>
    [Module(ModuleName = ModuleNames.ApplicationConfigModule, OnDemand = true)]
    public class ApplicationConfigModule : IModule
    {
        /// <summary>注册配置相关服务及 ConfigView 对话框入口。</summary>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
            containerRegistry.RegisterDialog<ConfigView>();
        }
        /// <summary>执行当前配置程序集声明的初始化行为。</summary>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
        }
    }
}

using Company.PlcHmi.Menu.Views;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Extension;
using Company.Core.Ioc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.PlcHmi.Menu
{
    /// <summary>
    /// 菜单模块的 Prism 入口，按需注册当前程序集，并将菜单视图挂到壳的菜单区域。
    /// 菜单命令的导航和权限判断由 MenuViewModel 处理。
    /// </summary>
    [Module(ModuleName = ModuleNames.PlcHmiMenuModule, OnDemand = true)]
    public class PlcHmiMenuModule : IModule
    {
        /// <summary>扫描当前程序集的约定注册，供容器解析菜单及其依赖。</summary>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
        }

        /// <summary>执行程序集初始化，并把 MenuView 注册为菜单区域的视图。</summary>
        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
            //注册试图到导航 方法2
            //containerProvider.Resolve<IRegionManager>().RegisterViewWithRegion<MenuView>(RegionNames.MenuRegion);
            //注册试图到导航 方法3
            // 使用全局 RegionManager 建立视图发现映射，由 MenuRegion 创建菜单视图。
            PrismProvider.RegionManager.RegisterViewWithRegion<MenuView>(RegionNames.MenuRegion);
        }
    }
}

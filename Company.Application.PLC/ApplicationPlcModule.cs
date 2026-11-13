using Company.Application.Share.Prism;
using Company.Core.Extension;
using Prism.Ioc;
using Prism.Modularity;
using System.Reflection;

namespace Company.Application.PLC;

/// <summary>PLC 应用模块入口；依赖配置模块完成绑定来源注册，再按程序集元数据注册并初始化运行服务。</summary>
[Module(ModuleName = ModuleNames.ApplicationPlcModule, OnDemand = true)]
[ModuleDependency(ModuleNames.ApplicationConfigModule)]
public sealed class ApplicationPlcModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
        => containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
    public void OnInitialized(IContainerProvider containerProvider)
        => containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
}

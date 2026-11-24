using Company.PlcHmi.Shared.Prism;
using Company.Core.Extension;
using Prism.Ioc;
using Prism.Modularity;
using System.Reflection;

namespace Company.PlcHmi.Plc;

/// <summary>PLC 应用模块入口；依赖配置模块完成绑定来源注册，再按程序集元数据注册并初始化运行服务。</summary>
[Module(ModuleName = ModuleNames.PlcHmiPlcModule, OnDemand = true)]
[ModuleDependency(ModuleNames.PlcHmiConfigurationModule)]
[ModuleDependency("PlcHmiPersistenceModule")]
[ModuleDependency(ModuleNames.PlcHmiLoginModule)] // 操作入口需要账户/会话服务；只注册，不要求用户已登录。
public sealed class PlcHmiPlcModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
        // 服务在后台程序集，模块现在位于装配程序集；必须扫描真实服务所在的程序集。
        => containerRegistry.RegisterAssembly(typeof(Company.PlcHmi.Plc.Connections.PlcConnectionManager).Assembly);
    public void OnInitialized(IContainerProvider containerProvider)
        => containerProvider.InitializeAssembly(typeof(Company.PlcHmi.Plc.Connections.PlcConnectionManager).Assembly);
}

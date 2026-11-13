using Company.Core.Extension;
using Prism.Ioc;
using Prism.Modularity;
using System.Reflection;

namespace Company.Hardware.ControlCard;

/// <summary>沿用原模块发现与注入，只注册选择服务，不初始化任何运动卡。</summary>
[Module(ModuleName = Name)]
public sealed class ControlCardModule : IModule
{
    public const string Name = "ControlCardModule";
    public void RegisterTypes(IContainerRegistry registry) => registry.RegisterAssembly(Assembly.GetExecutingAssembly());
    public void OnInitialized(IContainerProvider container) => container.InitializeAssembly(Assembly.GetExecutingAssembly());
}

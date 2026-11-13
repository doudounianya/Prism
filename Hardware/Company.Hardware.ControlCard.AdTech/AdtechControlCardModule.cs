using Prism.Ioc;
using Prism.Modularity;

namespace Company.Hardware.ControlCard.AdTech;

/// <summary>只注册命名驱动；不争用默认接口，也不在模块加载时初始化硬件。</summary>
[Module(ModuleName = "AdtechControlCardModule")]
[ModuleDependency(ControlCardModule.Name)]
public sealed class AdtechControlCardModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
        => registry.RegisterSingleton<IControlCard, AdtechControlCard>(nameof(ControlCardKind.AdTech));
    public void OnInitialized(IContainerProvider container) { }
}

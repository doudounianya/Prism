using Prism.Ioc;
using Prism.Modularity;

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>只注册命名驱动；不争用默认接口，也不在模块加载时初始化硬件。</summary>
[Module(ModuleName = "INOVANCEControlCardModule")]
[ModuleDependency(ControlCardModule.Name)]
public sealed class INOVANCEControlCardModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
        => registry.RegisterSingleton<IControlCard, INOVANCEControlCard>(nameof(ControlCardKind.INOVANCE));
    public void OnInitialized(IContainerProvider container) { }
}

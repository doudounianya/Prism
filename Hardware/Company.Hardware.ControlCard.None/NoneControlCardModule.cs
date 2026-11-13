using Prism.Ioc;
using Prism.Modularity;

namespace Company.Hardware.ControlCard.None;

/// <summary>只注册命名驱动；不争用默认接口，也不在模块加载时初始化硬件。</summary>
[Module(ModuleName = "NoneControlCardModule")]
[ModuleDependency(ControlCardModule.Name)]
public sealed class NoneControlCardModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
        => registry.RegisterSingleton<IControlCard, NoneControlCard>(nameof(ControlCardKind.None));
    public void OnInitialized(IContainerProvider container) { }
}

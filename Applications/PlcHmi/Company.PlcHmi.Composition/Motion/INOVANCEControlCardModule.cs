using Prism.Ioc;
using Prism.Modularity;

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>只注册命名驱动；不争用默认接口，也不在模块加载时初始化硬件。</summary>
[Module(ModuleName = "INOVANCEControlCardModule")]
[ModuleDependency(MotionControllerModule.Name)]
public sealed class INOVANCEControlCardModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
        => registry.RegisterSingleton<IMotionController, INOVANCEControlCard>(nameof(MotionDriverKind.Inovance));
    public void OnInitialized(IContainerProvider container) { }
}

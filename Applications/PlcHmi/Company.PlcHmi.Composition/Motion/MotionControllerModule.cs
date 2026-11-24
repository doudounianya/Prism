using Prism.Ioc;
using Prism.Modularity;
using System.Reflection;

namespace Company.Hardware.ControlCard;

/// <summary>沿用原模块发现与注入，只注册选择服务，不初始化任何运动卡。</summary>
[Module(ModuleName = Name)]
public sealed class MotionControllerModule : IModule
{
    public const string Name = "MotionControllerModule";
    // 只有装配层知道 Prism 的命名注册。接口与具体工厂共用一个容器单例，
    // Get/CloseAll 的缓存和关闭责任仍由原来的工厂承担。
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<MotionControllerFactory>(container =>
            new MotionControllerFactory(kind => container.Resolve<IMotionController>(kind.ToString())));
        registry.Register<IMotionControllerFactory>(container => container.Resolve<MotionControllerFactory>());
    }
    public void OnInitialized(IContainerProvider container) { }
}

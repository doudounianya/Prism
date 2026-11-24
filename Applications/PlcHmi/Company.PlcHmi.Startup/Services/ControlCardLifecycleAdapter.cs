using Company.Core.Ioc;
using Company.Hardware.ControlCard;
using Company.Core.Runtime;

namespace Company.PlcHmi.Startup.Services;

/// <summary>
/// 将可选运动卡的退出责任登记到统一协调器，不自动选卡或初始化 SDK。
/// 控制卡启用及业务页面适配需要显式配置，单纯加入工程不会下载设备参数。
/// </summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class ControlCardLifecycleAdapter : IDisposable
{
    private readonly IMotionControllerFactory cards;
    public ControlCardLifecycleAdapter(IApplicationLifecycle lifecycle, IMotionControllerFactory cards)
    {
        this.cards = cards;
        // 不选择控制卡、不初始化 SDK；只在最终退出时关闭工厂已解析的实例。
        lifecycle.Register(new("正在准备设备生命周期...", -10, 40, _ => Task.CompletedTask,
            () => Task.Run(cards.CloseAll)));
    }
    public void Dispose()
    {
        cards.CloseAll();
    }
}

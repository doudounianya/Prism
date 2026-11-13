using Company.Application.Share.Events;
using Company.Core.Ioc;
using Company.Hardware.ControlCard;
using Prism.Events;

namespace Company.Application.Initialize.Services;

/// <summary>
/// 将可选运动卡接入既有关闭事件。不自动选卡、不读写设备，不改变 PLC 启动、报警或产能服务。
/// 控制卡启用及业务页面适配需要显式配置，单纯加入工程不会下载设备参数。
/// </summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class ControlCardLifecycleAdapter : IDisposable
{
    private readonly CloseAllHardwareEvent close;
    private readonly SubscriptionToken token;
    private readonly IControlCardFactory cards;
    public ControlCardLifecycleAdapter(IEventAggregator events, IControlCardFactory cards)
    {
        this.cards = cards;
        close = events.GetEvent<CloseAllHardwareEvent>();
        token = close.Subscribe(cards.CloseAll);
    }
    public void Dispose()
    {
        close.Unsubscribe(token);
        cards.CloseAll();
    }
}

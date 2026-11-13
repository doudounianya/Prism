using Company.Core.Ioc;
using Company.Logger;
using Prism.Ioc;

namespace Company.Hardware.ControlCard;

/// <summary>固定的实现标识，只决定使用哪个驱动，不代表允许连接或动作。</summary>
public enum ControlCardKind { None, AdTech, INOVANCE }

public interface IControlCardFactory
{
    /// <summary>解析已命名注册的唯一单例。调用者仍须提供显式启用的配置才能初始化。</summary>
    IControlCard Get(ControlCardKind kind);
    /// <summary>仅关闭已经解析过的对象，绝不为关闭而创建另一个真实驱动。</summary>
    void CloseAll();
}

/// <summary>
/// 在现有 Prism 容器中选择控制卡，消除多个模块竞争默认 IControlCard 的顺序依赖。
/// 构造、模块注册和 Get 均不调用 Init；原生库只会在明确启用后的驱动初始化中使用。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IControlCardFactory))]
public sealed class ControlCardFactory(IContainerProvider container) : IControlCardFactory, IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<ControlCardKind, IControlCard> resolved = new();
    private bool disposed;

    public IControlCard Get(ControlCardKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (resolved.TryGetValue(kind, out var existing)) return existing;
            var card = container.Resolve<IControlCard>(kind.ToString());
            resolved.Add(kind, card);
            return card;
        }
    }

    public void CloseAll()
    {
        IControlCard[] cards;
        lock (sync) cards = resolved.Values.Distinct().ToArray();
        // 每个对象独立清理，一张卡失败不能阻断另一张卡的关闭。
        foreach (var card in cards)
            try { card.Close(); }
            catch (Exception ex) { Logs.LogError("控制卡关闭失败：" + ex.Message); }
    }

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        CloseAll();
    }
}

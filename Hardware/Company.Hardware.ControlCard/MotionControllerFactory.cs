using Company.Logger;

namespace Company.Hardware.ControlCard;

/// <summary>固定的实现标识，只决定使用哪个驱动，不代表允许连接或动作。</summary>
public enum MotionDriverKind { Simulation = 0, Inovance = 2 }

public interface IMotionControllerFactory
{
    /// <summary>解析已命名注册的唯一单例。调用者仍须提供显式启用的配置才能初始化。</summary>
    IMotionController Get(MotionDriverKind kind);
    /// <summary>仅关闭已经解析过的对象，绝不为关闭而创建另一个真实驱动。</summary>
    void CloseAll();
}

/// <summary>
/// 通过宿主提供的解析函数选择运动控制器；运行库不依赖 Prism 或具体厂商。
/// 构造、模块注册和 Get 均不调用 Initialize；原生库只会在明确启用后的驱动初始化中使用。
/// </summary>
public sealed class MotionControllerFactory : IMotionControllerFactory, IDisposable
{
    private readonly Func<MotionDriverKind, IMotionController> resolve;

    /// <summary>解析策略由应用装配层提供；构造函数不会创建或连接任何控制卡。</summary>
    public MotionControllerFactory(Func<MotionDriverKind, IMotionController> resolve)
        => this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    private readonly object sync = new();
    private readonly Dictionary<MotionDriverKind, IMotionController> resolved = new();
    private bool disposed;

    public IMotionController Get(MotionDriverKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (resolved.TryGetValue(kind, out var existing)) return existing;
            var card = resolve(kind);
            resolved.Add(kind, card);
            return card;
        }
    }

    public void CloseAll()
    {
        IMotionController[] cards;
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

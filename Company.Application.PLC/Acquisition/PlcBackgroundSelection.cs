using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.Application.PLC.Acquisition;

/// <summary>应用业务可缩小常驻组的实际读取范围；不改变Excel绑定、协议或页面范围。</summary>
/// <remarks>每组最多一个策略；策略返回值只能是已绑定变量的子集，防止业务策略绕过配置生成新地址。</remarks>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcBackgroundSelection
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Func<string, IReadOnlyCollection<PlcVariableKey>, DateTimeOffset, IReadOnlyCollection<PlcVariableKey>>> _selectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _resets = new(StringComparer.Ordinal);
    /// <summary>注册组级动态选择器及可选运行重置回调；返回句柄释放后移除本次注册。</summary>
    public IDisposable Register(string group, Func<string, IReadOnlyCollection<PlcVariableKey>, DateTimeOffset, IReadOnlyCollection<PlcVariableKey>> selector, Action? reset = null)
    {
        lock (_sync) {
            if (!_selectors.TryAdd(group, selector)) throw new InvalidOperationException("采集组已安装读取策略：" + group);
            if (reset is not null) _resets.Add(group, reset);
        }
        return new Registration(() => { lock (_sync) if (_selectors.GetValueOrDefault(group) == selector) { _selectors.Remove(group); _resets.Remove(group); } });
    }
    internal void Reset() { Action[] resets; lock (_sync) resets = _resets.Values.ToArray(); foreach (var reset in resets) reset(); }
    internal bool Managed(string group) { lock (_sync) return _selectors.ContainsKey(group); }
    internal IReadOnlyCollection<PlcVariableKey> Select(string device, string group, IReadOnlyCollection<PlcVariableKey> available, DateTimeOffset now)
    {
        Func<string, IReadOnlyCollection<PlcVariableKey>, DateTimeOffset, IReadOnlyCollection<PlcVariableKey>>? selector;
        lock (_sync) selector = _selectors.GetValueOrDefault(group);
        if (selector is null) return available;
        // 先在锁外调用业务选择器，避免持有注册表锁执行外部代码；结果去重后校验仍属于当前可用集合。
        var selected = selector(device, available, now).ToHashSet();
        if (selected.Except(available).Any()) throw new InvalidOperationException("读取策略包含未绑定的变量。");
        return selected;
    }
    private sealed class Registration(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}

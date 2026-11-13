/*
 * 文件职责与维护说明：
 * 页面级显示缓存：LastValid 保留上次有效内容，Current 只接受当前申请之后、当前配置和连接代次的数据。
 * 重新显示时可先显示旧值，但旧值不会被当作允许写入的最新依据。
 */
using Company.Application.Share.PLC.Runtime.Models;

namespace Company.Application.Main.Models;

/// <summary>页面显示缓存；每次激活必须取得本次申请之后、当前配置及连接代次的有效值。</summary>
internal sealed class PageValueCache
{
    private DateTimeOffset _requestedAt = DateTimeOffset.MaxValue;
    private long _epoch;
    private string _stamp = "";
    public PlcVariableSnapshot? LastValid { get; private set; }
    public PlcVariableSnapshot? Current { get; private set; }
    public bool IsFresh => Current?.IsValid == true;
    public string PendingReason { get; private set; } = "等待刷新";
    // 开始新一轮读取时保留 LastValid 供先行显示，但清空 Current，尚不能据此放开写入。
    public void Begin(long epoch, string stamp, DateTimeOffset requestedAt)
    {
        _epoch = epoch; _stamp = stamp; _requestedAt = requestedAt;
        Current = null; PendingReason = "等待刷新";
    }
    // 隐藏或失效后把时间门槛设为最大值，使现存旧样本不能自动变成新一轮有效值。
    public void Pause(string reason)
    { Current = null; _requestedAt = DateTimeOffset.MaxValue; PendingReason = reason; }
    // 同时校验非空、连接代次、配置戳和本轮申请时间。
    // 旧连接或旧配置的快照即使数值有效，也不能作为当前页面的最新依据。
    public void Observe(PlcVariableSnapshot? snapshot)
    {
        if (snapshot is null || _epoch <= 0 || snapshot.ConnectionEpoch != _epoch ||
            snapshot.ConfigurationVersion != _stamp || snapshot.LastAttemptAt is not { } at || at < _requestedAt)
        { Current = null; return; }
        Current = snapshot;
        if (snapshot.IsValid) LastValid = snapshot;
    }
}

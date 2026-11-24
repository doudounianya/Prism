using Company.PlcHmi.Shared.PLC.History;
using Company.PlcHmi.Shared.PLC.History.Models;
using Company.Core.Ioc;
namespace Company.PlcHmi.Main.Services.History;

/// <summary>
/// 通用小时缓存尚无实际来源时的显式占位实现；声明不可用并拒绝读取，避免由当前计数伪造历史。
/// </summary>
[ExposedService(Lifetime.Singleton,typeof(IProductionHistorySource))]
public sealed class UnavailableProductionHistorySource : IProductionHistorySource
{
    public bool Available => false;
    /// <summary>
    /// 返回失败任务说明未配置小时缓存；不访问 PLC，也不生成零值快照。
    /// </summary>
    public Task<ProductionHistorySnapshot> ReadAsync(CancellationToken token) =>
        Task.FromException<ProductionHistorySnapshot>(new InvalidOperationException("PLC小时缓存结构尚未配置；不会使用当前计数伪造历史。"));
}

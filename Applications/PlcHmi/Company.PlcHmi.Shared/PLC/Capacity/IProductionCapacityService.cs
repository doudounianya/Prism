using Company.PlcHmi.Shared.PLC.Capacity.Models;

namespace Company.PlcHmi.Shared.PLC.Capacity;

/// <summary>提供设备当前产能观测快照及显式刷新入口；持久化小时历史另由历史服务负责。</summary>
public interface IProductionCapacityService
{
    event EventHandler? Changed;
    /// <summary>读取当前已有快照，不以读取接口本身承诺产生新的 PLC 采样。</summary>
    ProductionCapacitySnapshot GetSnapshot(string deviceId);
    /// <summary>请求刷新并返回新的观测结果；调用方仍须检查 State 和警告后使用其中数量。</summary>
    Task<ProductionCapacitySnapshot> RefreshAsync(string deviceId, CancellationToken token = default);
}

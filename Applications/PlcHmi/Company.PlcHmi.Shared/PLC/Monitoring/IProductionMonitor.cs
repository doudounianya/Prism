using Company.PlcHmi.Shared.PLC.Monitoring.Models;

namespace Company.PlcHmi.Shared.PLC.Monitoring;

/// <summary>PLC独立原始计数，不推导日产量、班次或质量口径。通知在后台线程发出。</summary>
/// <remarks>消费者应同时读取状态和时间；LastValidValue 只代表历史有效观测，不能在断线后当作当前产量。</remarks>
public interface IProductionMonitor
{
    /// <summary>取得本次运行的各原始计数观测，不触发历史补算或数据库查询。</summary>
    IReadOnlyList<ProductionCounterSnapshot> GetSnapshots();
    event EventHandler? Changed;
}

using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.Persistence.Models;
namespace Company.PlcHmi.Shared.Persistence;

public interface IProductionHistoryRepository
{
    /// <summary>按设备、时间段开始、产能编号比较；旧版本及来源冲突不得覆盖。</summary>
    Task<ProductionApplyResult> ApplyAsync(string streamId, long sequence,
        IReadOnlyList<PlcProductionHour> records, PlcProductionSyncState state, CancellationToken token);
}

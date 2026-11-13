using Company.Database.PLC.Entities;
using Company.Database.PLC.Models;
namespace Company.Database.PLC;

public interface IProductionHistoryRepository
{
    /// <summary>按设备、时间段开始、产能编号比较；旧版本及来源冲突不得覆盖。</summary>
    Task<ProductionApplyResult> ApplyAsync(string streamId, long sequence,
        IReadOnlyList<PlcProductionHour> records, PlcProductionSyncState state, CancellationToken token);
}

using Company.PlcHmi.Shared.PLC.History.Models;
namespace Company.PlcHmi.Shared.PLC.History;
/// <summary>小时历史缓存的只读来源，不使用当前 Production[n] 推算历史。</summary>
/// <remarks>来源负责提供版本、完整性与来源世代等一致性信息，供同步服务判断是否可持久化。</remarks>
public interface IProductionHistorySource
{
    /// <summary>当前是否具有已配置的历史来源；不能用实时产能显示可用代替该能力。</summary>
    bool Available {get;}
    /// <summary>读取小时历史及其一致性标记；调用方须校验快照完整性和版本，不可仅凭任务完成就提交。</summary>
    Task<ProductionHistorySnapshot> ReadAsync(CancellationToken token);
}

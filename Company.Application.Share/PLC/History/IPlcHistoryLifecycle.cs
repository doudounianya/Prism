using Company.Application.Share.PLC.History.Models;
namespace Company.Application.Share.PLC.History;
/// <summary>历史持久化流程的生命周期入口；与 PLC 通讯运行状态独立，向界面暴露队列和提交状态。</summary>
public interface IPlcHistoryLifecycle
{
    /// <summary>历史流程的当前状态快照，包括未提交数量、最近提交时间及产能同步信息。</summary>
    PlcHistoryStatus Status {get;}
    Task StartAsync(CancellationToken token = default);
    Task StopAsync(CancellationToken token = default);
}

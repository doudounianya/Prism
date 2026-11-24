using Company.PlcHmi.Shared.PLC.Runtime.Models;
namespace Company.PlcHmi.Shared.PLC.Runtime;

/// <summary>管理读取需求与后台业务消费者；页面通过范围控制实际读取变量，数据展示另从缓存提供者取快照。</summary>
public interface IPlcAcquisitionService
{
    /// <summary>仅允许页面生命周期的可读变量；空 Variables 表示不读任何变量。</summary>
    Task<IPlcAcquisitionScope> OpenPageScopeAsync(PlcAcquisitionRequest request, CancellationToken cancellationToken = default);
    /// <summary>建立临时需求并等待至少一次尝试，返回逐项状态；取消会释放临时范围，不保证所有结果有效。</summary>
    Task<IReadOnlyList<PlcVariableSnapshot>> ReadOnceAsync(IReadOnlyCollection<PlcVariableKey> variables, CancellationToken cancellationToken = default);
    /// <summary>按组注册后台帧/缺口消费者；释放返回令牌即可注销，注册本身不会激活额外变量。</summary>
    IDisposable RegisterConsumer(IPlcSampleConsumer consumer, IReadOnlyCollection<string> groupIds);
}

/// <summary>页面需求的所有权句柄；离开页面时异步释放，范围更新与刷新各自推进版本以隔离旧轮次。</summary>
public interface IPlcAcquisitionScope : IAsyncDisposable
{
    /// <summary>创建时固定的范围所有者标识，更新请求不能更换该所有者。</summary>
    string OwnerId { get; }
    /// <summary>范围当前版本；旧版本读取完成不能满足新版本的待尝试集合。</summary>
    long Version { get; }
    /// <summary>替换本范围的明确变量集合并重新请求首次尝试；不允许把后台变量纳入页面范围。</summary>
    Task UpdateAsync(PlcAcquisitionRequest request, CancellationToken cancellationToken = default);
    /// <summary>强制一次新尝试；完成不等同于所有变量有效。调用方取消等待不会撤销其他需求。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

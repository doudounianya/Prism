using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;

namespace Company.Application.PLC.Acquisition;

/// <summary>页面采集需求的可释放句柄；具体变量、版本及等待状态由单例采集服务统一维护。</summary>
public sealed class PlcAcquisitionScope : IPlcAcquisitionScope
{
    private readonly PlcAcquisitionService _owner;
    private int _disposed;
    internal Guid Id { get; }
    public string OwnerId { get; }
    /// <summary>当前范围版本；更新或强制刷新都会推进版本，旧采集轮次不能完成新版本的等待。</summary>
    public long Version => _owner.Version(Id);
    internal PlcAcquisitionScope(PlcAcquisitionService owner, Guid id, string ownerId)
    { _owner = owner; Id = id; OwnerId = ownerId; }
    public Task UpdateAsync(PlcAcquisitionRequest request, CancellationToken cancellationToken = default)
        => _owner.Update(Id, request, cancellationToken);
    public Task RefreshAsync(CancellationToken cancellationToken = default) => _owner.Refresh(Id, cancellationToken);
    public ValueTask DisposeAsync()
    {
        // 释放必须幂等；同一页面的重复退出只撤销一次需求，其他范围共享的变量仍可继续采集。
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(Id);
        return ValueTask.CompletedTask;
    }
}

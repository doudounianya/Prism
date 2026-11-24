using Company.PlcHmi.Shared.PLC.History.Models;
using Company.PlcHmi.Shared.Persistence;
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.Core.Ioc;
using Company.Logger;
using System.IO;

namespace Company.PlcHmi.Main.Services.History;
/// <summary>
/// 后台顺序消费本地日志，将报警或产能事实交给数据库仓储。
/// 任何失败都保留队首供重试；仅在数据库操作成功后推进本地提交位置。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcHistoryWriter(PlcPersistenceJournal journal,AlarmHistoryService alarm,
    IProductionHistoryRepository production,ProductionHistorySnapshotValidator validator,PlcHistoryOptions options,
    CapacityProductionHistoryValidator capacityValidator)
{
    private CancellationTokenSource? _stop;
    private Task _worker=Task.CompletedTask;
    private string? _error;
    private long _lastCommittedTicks;
    private ProductionHistorySyncResult? _production;
    public string? Error=>Volatile.Read(ref _error);
    public DateTime? LastCommittedUtc { get { long ticks=Interlocked.Read(ref _lastCommittedTicks); return ticks==0?null:new DateTime(ticks,DateTimeKind.Utc); } }
    public ProductionHistorySyncResult? Production=>Volatile.Read(ref _production);
    /// <summary>
    /// 启动单个消费任务；运行中的任务不能重复启动。
    /// </summary>
    public void Start()
    {
        if(!_worker.IsCompleted)throw new InvalidOperationException("历史写入器已经运行。");
        _stop=new();_worker=Task.Run(()=>Run(_stop.Token));
    }
    /// <summary>
    /// 持续重放队首日志；流标识与序号交由仓储用于识别已提交项，处理数据库成功而本地检查点失败的重试。
    /// </summary>
    private async Task Run(CancellationToken token)
    {
        while(!token.IsCancellationRequested){
            try{
                // Peek 只查看而不出队；数据库失败、功能开关关闭或载荷校验失败都会阻塞此项并保留后续顺序。
                var entry=await journal.PeekAsync(token).ConfigureAwait(false);
                if(entry is null){await Task.Delay(50,token).ConfigureAwait(false);continue;}
                if(entry.Kind=="Alarm"){
                    if(!options.AlarmCaptureEnabled)throw new AlarmCaptureDisabledException();
                    await alarm.ApplyAsync(entry,token).ConfigureAwait(false);
                }
                else if(entry.Kind=="CapacityProduction"){
                    if(!options.ProductionSyncEnabled)throw new ProductionSyncDisabledException();
                    var batch=entry.CapacityProduction??throw new InvalidDataException("产能观察批次为空。");
                    // 入库前再次校验可重放载荷，防止将不符合归班、时间及批次约束的数据交给仓储。
                    var records=capacityValidator.Validate(batch);
                    var state=new PlcProductionSyncState{DeviceId=batch.DeviceId,SourceId=Company.PlcHmi.Shared.Persistence.Models.ProductionSourcePolicy.CapacityDay,
                        LastAttemptUtc=Company.PlcHmi.Shared.Persistence.Models.ProductionSourcePolicy.Milliseconds(batch.ConfirmedUtc),
                        AvailableFromUtc=records.Count==0?null:records.Min(r=>r.PeriodStartUtc),
                        AvailableToUtc=records.Count==0?null:records.Max(r=>r.PeriodEndUtc)};
                    var result=await production.ApplyAsync(entry.StreamId,entry.Sequence,records,state,token).ConfigureAwait(false);
                    _production=new(result.AlreadyApplied?"AlreadyCommitted":result.Rejected>0?"Partial":"Committed",entry.Sequence,0,result.Error,result);
                }
                else if(entry.Kind=="Production"){
                    if(!options.ProductionSyncEnabled)throw new ProductionSyncDisabledException();
                    var snapshot=entry.Production??throw new InvalidDataException("小时快照为空。");
                    var records=validator.Validate(snapshot,out int invalid);
                    var state=new PlcProductionSyncState{DeviceId=snapshot.DeviceId,SourceId=snapshot.SourceId,
                        LastAttemptUtc=snapshot.CapturedUtc,Rejected=invalid*10,
                        AvailableFromUtc=records.Count==0?null:records.Min(x=>x.PeriodStartUtc),
                        AvailableToUtc=records.Count==0?null:records.Max(x=>x.PeriodEndUtc)};
                    var result=await production.ApplyAsync(entry.StreamId,entry.Sequence,records,state,token).ConfigureAwait(false);
                    _production=new(result.AlreadyApplied?"AlreadyCommitted":result.Rejected>0?"Partial":"Committed",
                        entry.Sequence,invalid,result.Error,result);
                }else throw new InvalidDataException("不支持的历史日志类型。");
                // 数据库事务已提交，才允许推进本地位置。
                // 若此处写检查点失败，下次仍重放同一序号；数据库侧需按日志流/序号识别重复提交。
                await journal.CommitAsync(entry.Sequence,token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastCommittedTicks,DateTime.UtcNow.Ticks);_error=null;
            }catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception ex){
                var message=ex is AlarmCaptureDisabledException ? "报警历史保存未启用；旧报警日志保留，请使用产能专用日志目录。" : ex is ProductionSyncDisabledException
                    ? "产能同步未启用，产能日志保留且不推进提交位置；请检查联调日志目录。"
                    : "历史入库尚未完成："+ex.GetType().Name+"；本地日志保留，将重试。";
                // 同类持续失败只在错误文本变化时告警，重试间隔仍使用可取消延时。
                if(_error!=message)Logs.LogWarning(message);
                _error=message;
                try{await Task.Delay(options.RetryIntervalMs,token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            }
        }
    }
    /// <summary>
    /// 立即停止重试和后续取项，等待正在执行的事务返回后退出。
    /// 事实已在 AppendAsync 刷盘；不在关窗时强行排空数据库队列，未提交项保留原序号供下次幂等重放。
    /// </summary>
    public async Task StopAsync(CancellationToken token=default)
    {
        if(_stop is null)return;
        _stop.Cancel();
        // 不能只超时返回并释放 journal：底层事务可能尚在结束，仍须由本任务持有并等待。
        await _worker.ConfigureAwait(false);_stop.Dispose();_stop=null;
    }
    private sealed class ProductionSyncDisabledException : InvalidOperationException { }
    private sealed class AlarmCaptureDisabledException : InvalidOperationException { }
}

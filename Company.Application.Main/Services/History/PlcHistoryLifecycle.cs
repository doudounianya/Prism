using Company.Application.Share.Events;
using Company.Application.Share.PLC.History;
using Company.Application.Share.PLC.History.Models;
using Company.Application.Share.PLC.Runtime;
using Company.Core.Ioc;
using Company.Logger;
using Prism.Events;

namespace Company.Application.Main.Services.History;
/// <summary>
/// 协调历史日志、采集捕获、入库任务和产能同步的启动停止，并汇总可供界面显示的保存状态。
/// </summary>
[ExposedService(Lifetime.Singleton,typeof(IPlcHistoryLifecycle),AutoInitialize=true)]
public sealed class PlcHistoryLifecycle:IPlcHistoryLifecycle,IDisposable
{
    private readonly PlcHistoryOptions _options;
    private readonly PlcPersistenceJournal _journal;
    private readonly PlcHistoryCaptureService _capture;
    private readonly PlcHistoryWriter _writer;
    private readonly ProductionHistorySyncService _production;
    private readonly IPlcRuntime _runtime;
    private readonly CloseAllHardwareEvent _close;
    private readonly SubscriptionToken _subscription;
    private readonly SemaphoreSlim _gate=new(1,1);
    private volatile string _state="Disabled";
    private string? _error;
    private bool _started;
    /// <summary>
    /// 合并捕获故障、入库重试、待提交数量及产能结果；Queued 与数据库已提交状态按日志序号衔接。
    /// </summary>
    public PlcHistoryStatus Status
    {
        get{
            var sync=_production.LastResult;var saved=_writer.Production;
            if(saved is not null&&(sync.State=="Disabled"||(sync.State=="Queued"&&saved.Sequence>=sync.Sequence)))sync=saved;
            return new(_capture.Error!=null?"CaptureFault":_writer.Error!=null&&_started?"Retrying":_state,
                _journal.Pending,_capture.Error??_error??(_started?_writer.Error:null)??sync.Error,_writer.LastCommittedUtc,sync)
            {
                JournalBytes=_journal.JournalBytes,JournalCapacityBytes=_journal.MaximumJournalBytes,
                MaximumPending=_options.MaximumPending,JournalError=_journal.LastAppendError
            };
        }
    }
    /// <summary>
    /// 保存依赖并订阅全局硬件关闭事件；实际后台任务由 StartAsync 显式启动。
    /// </summary>
    public PlcHistoryLifecycle(PlcHistoryOptions options,PlcPersistenceJournal journal,PlcHistoryCaptureService capture,
        PlcHistoryWriter writer,ProductionHistorySyncService production,IPlcRuntime runtime,IEventAggregator events)
    {
        _options=options;_journal=journal;_capture=capture;_writer=writer;_production=production;_runtime=runtime;
        _close=events.GetEvent<CloseAllHardwareEvent>();_subscription=_close.Subscribe(Close);
    }
    /// <summary>
    /// 串行初始化日志，再开放捕获并启动写入器与同步定时器；失败时按已启动程度回收。
    /// </summary>
    public async Task StartAsync(CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(_started)return;
            if(!_options.Enabled){_state="Disabled";return;}
            _options.Validate();_state="Starting";
            // 必须先恢复旧日志并取得目录所有权，才能接受新事实；否则无法维持同一流的连续序号。
            await _journal.InitializeAsync(token).ConfigureAwait(false);
            if(_options.AlarmCaptureEnabled)_capture.BeginRun();
            _writer.Start();_started=true;_state="Running";_error=null;
            _production.StartTimer();
        }catch(Exception ex){
            _state="Faulted";_error="历史服务启动失败："+ex.GetType().Name;
            await _capture.SuspendAsync().ConfigureAwait(false);
            if(_started){await _production.StopTimerAsync().ConfigureAwait(false);await _writer.StopAsync().ConfigureAwait(false);_started=false;}
            await _journal.DisposeAsync().ConfigureAwait(false);
            if(ex is OperationCanceledException)throw;
            Logs.LogWarning(_error);
        }finally{_gate.Release();}
    }
    /// <summary>
    /// 先停止运行采集和产能定时转移，再关闭报警接收、等待写入器并释放日志目录锁。
    /// </summary>
    public async Task StopAsync(CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!_started)return;_state="Stopping";
            // 不依赖 CloseAllHardwareEvent 各订阅者的执行顺序。
            try{
                try{await _runtime.StopAsync().ConfigureAwait(false);}
                finally{await _production.StopTimerAsync().ConfigureAwait(false);}
            }finally{
                await _capture.SuspendAsync().ConfigureAwait(false);
                await _writer.StopAsync().ConfigureAwait(false);
                // 日志待提交与来源待落盘是两层队列；任一非空都不能把关闭状态称为全部保存。
                _started=false;_state=_journal.Pending>0||_production.PendingConfirmed>0?"StoppedWithPending":"Stopped";
                if(_production.PendingConfirmed>0)Logs.LogWarning("仍有已确认产能批次未能写入本地日志；下次启动必须重新核对PLC缓存，不能视为全部保存。");
                await _journal.DisposeAsync().ConfigureAwait(false);
            }
        }finally{_gate.Release();}
    }
    /// <summary>
    /// 将同步关闭事件桥接至异步停止，使用配置的总等待预算并记录超时或异常。
    /// </summary>
    private void Close()
    {
        try{
            if(!Task.Run(()=>StopAsync()).Wait(_options.ShutdownTimeoutMs))
                Logs.LogWarning("历史服务停止超时；本地待提交日志保留，不能视为全部入库。");
        }catch(Exception ex){Logs.LogWarning("历史服务停止失败："+ex.GetType().Name);}
    }
    /// <summary>
    /// 取消事件订阅并执行历史服务关闭。
    /// </summary>
    public void Dispose(){_close.Unsubscribe(_subscription);Close();}
}

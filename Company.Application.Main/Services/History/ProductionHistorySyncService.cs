using Company.Application.Main.Services.History.Models;
using Company.Application.Share.PLC.History;
using Company.Application.Share.PLC.History.Models;
using Company.Core.Ioc;

namespace Company.Application.Main.Services.History;

/// <summary>
/// 将已由相邻真实采样确认的产能批次转入可靠本地日志。
/// 事件唤醒与周期检查共用串行入口，成功追加之前不确认来源队列。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IProductionHistorySyncService))]
public sealed class ProductionHistorySyncService(CapacityProductionHistorySource source, CapacityProductionHistoryValidator validator,
    PlcPersistenceJournal journal, PlcHistoryOptions options) : IProductionHistorySyncService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // 容量为 1 的信号合并密集 BatchReady 通知；循环会持续取批，直到当前没有可排队项。
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task _timer = Task.CompletedTask;
    private ProductionHistorySyncResult _last = new("Disabled");
    public ProductionHistorySyncResult LastResult => Volatile.Read(ref _last);
    public int PendingConfirmed => source.PendingCount;
    /// <summary>
    /// 尝试转移一个队首批次；禁用、忙碌、等待和拒绝均用状态返回，取消仍向调用方传播。
    /// </summary>
    public async Task<ProductionHistorySyncResult> SyncOnceAsync(CancellationToken token = default)
    {
        if (!options.Enabled || !options.ProductionSyncEnabled || !journal.Initialized) return _last = new("Disabled");
        // 不等待另一轮同步，避免手动触发与后台定时器重复读取并追加同一队首。
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return new("Busy");
        try
        {
            if (!source.Available) return _last = new("NoSource", Error: "产能完整绑定尚未配置。");
            var batch = await source.ReadAsync(token).ConfigureAwait(false);
            if (batch is null) return new("Waiting");
            // 验证结果仅用于确认批次可入库；日志保存原始确认事实，写入器重放时还会再次校验。
            var records = validator.Validate(batch);
            if (records.Count == 0) return _last = new("NoData", Error: "暂无相邻确认的已结束小时；不会补零。");
            long sequence = await journal.AppendAsync(new() { Kind = "CapacityProduction", CapacityProduction = batch }, token).ConfigureAwait(false);
            source.Acknowledge(batch); // 先可靠写入本地日志，之后才从待结算队列移除。
            return _last = new("Queued", sequence);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return _last = new("Rejected", Error: "产能批次未可靠排队：" + ex.GetType().Name + "；已有记录未覆盖。"); }
        finally { _gate.Release(); }
    }
    /// <summary>
    /// 订阅新批次通知并启动后台转移循环；每次唤醒先排尽当前可用批次，再等待下一信号或超时。
    /// </summary>
    public void StartTimer()
    {
        if (!options.Enabled || !options.ProductionSyncEnabled) return;
        if (!_timer.IsCompleted) throw new InvalidOperationException("产能同步定时器已经运行。");
        _stop = new(); var token = _stop.Token;
        source.BatchReady += Wake;
        // 初始化先于PLC启动；没有采样时持续等待，不能永久停留在NoSource。
        _timer = Task.Run(async () => {
            try { while (!token.IsCancellationRequested) {
                while ((await SyncOnceAsync(token).ConfigureAwait(false)).State == "Queued") { }
                await _wake.WaitAsync(options.ProductionSyncIntervalMs!.Value, token).ConfigureAwait(false);
            } }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }
    /// <summary>
    /// 取消循环并解除订阅，再以独立超时尝试将最后已确认批次追加到日志。
    /// </summary>
    public async Task StopTimerAsync()
    {
        source.BatchReady -= Wake;
        _stop?.Cancel();
        try {
            await _timer.ConfigureAwait(false);
            // 调用方已先停止PLC；最后一个确认批次同样必须落到本地日志。
            using var timeout = new CancellationTokenSource(options.ShutdownTimeoutMs);
            while ((await SyncOnceAsync(timeout.Token).ConfigureAwait(false)).State == "Queued") { }
        } finally { _stop?.Dispose(); _stop = null; }
    }
    // 信号已存在时不再累计；这只合并通知，不丢弃来源队列中的已确认事实。
    private void Wake(object? sender, EventArgs args) { try { _wake.Release(); } catch (SemaphoreFullException) { } }
}

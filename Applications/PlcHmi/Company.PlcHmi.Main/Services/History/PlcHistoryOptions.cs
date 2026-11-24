using Company.Core.Ioc;
using System.IO;
namespace Company.PlcHmi.Main.Services.History;

/// <summary>
/// 历史持久化的开关、日志目录、容量与关闭/重试预算。
/// 默认禁用；Debug 环境变量仅用于显式开启隔离目录中的联调保存。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed record PlcHistoryOptions
{
    // 默认关闭；联调开关仅作用于 Debug 进程，不修改正式配置。
    public PlcHistoryOptions()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("CRT_HMI_ENABLE_ALARM_HISTORY") == "1")
        {
            Enabled = true;
            AlarmCaptureEnabled = true;
            // 必须显式指定隔离目录；缺失时由 Validate 阻止启动历史服务。
            JournalDirectory = Environment.GetEnvironmentVariable("CRT_HMI_ALARM_HISTORY_JOURNAL") ?? string.Empty;
        }
        if (Environment.GetEnvironmentVariable("CRT_HMI_ENABLE_CAPACITY_SYNC") == "1")
        {
            Enabled = true;
            ProductionSyncEnabled = true;
            ProductionSyncIntervalMs = 60000;
            JournalDirectory = Environment.GetEnvironmentVariable("CRT_HMI_CAPACITY_SYNC_JOURNAL") ?? string.Empty;
        }
#endif
    }

    // 总开关控制生命周期；报警捕获与产能同步还分别检查各自功能开关。
    public bool Enabled {get;init;} = false;
    public bool AlarmCaptureEnabled {get;init;} = false;
    public bool ProductionSyncEnabled {get;init;} = false;
    // 启用产能同步时必须配置不少于 100ms 的检查间隔；新批次事件可在定时到期前主动唤醒。
    public int? ProductionSyncIntervalMs {get;init;}
    public string JournalDirectory {get;init;} = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CRT_HMI","PLC","History");
    public int RetryIntervalMs {get;init;} = 2000;
    public int ShutdownTimeoutMs {get;init;} = 10000;
    // 同时限制未提交条数与日志字节数，数据库长时间不可用时拒绝继续确认新事实。
    public int MaximumPending {get;init;} = 100000;
    public long MaximumJournalBytes {get;init;} = 256L*1024*1024;
    /// <summary>
    /// 校验绝对目录、时间预算和容量下限；这里只检查选项，不创建目录或连接数据库。
    /// </summary>
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(JournalDirectory) || RetryIntervalMs < 10 || ShutdownTimeoutMs < 100 ||
            MaximumPending < 1 || MaximumJournalBytes < 4096 ||
            (ProductionSyncEnabled && ProductionSyncIntervalMs is not >= 100))
            throw new ArgumentException("历史服务目录、容量或时间配置无效。");
    }
}

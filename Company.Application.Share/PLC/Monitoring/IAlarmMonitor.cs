using Company.Application.Share.PLC.Monitoring.Models;

namespace Company.Application.Share.PLC.Monitoring;

/// <summary>仅提供本次运行的实时状态，不代表持久化报警历史。通知在后台线程发出。</summary>
/// <remarks>界面收到 Changed 后应切换到 UI 线程并重新取快照；不得以合并通知次数代替报警发生次数。</remarks>
public interface IAlarmMonitor
{
    /// <summary>读取当前内存报警观测；断线或缺口期间 Active 可为空，应显示未知状态。</summary>
    IReadOnlyList<AlarmRuntimeSnapshot> GetSnapshots();
    event EventHandler? Changed;
}

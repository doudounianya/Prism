namespace Company.Core.Runtime;

/// <summary>应用后台状态。页面预热和导航不属于硬件启动步骤，由桌面层在 Running 后完成。</summary>
public enum ApplicationRunState { Created, Starting, Running, Stopping, Stopped, Faulted }

/// <summary>应用装配时登记的生命周期步骤。启动按 StartOrder 升序，退出按 StopOrder 升序。</summary>
public sealed record ApplicationLifecycleStep(string Name, int StartOrder, int StopOrder,
    Func<CancellationToken, Task> Start, Func<Task> Stop);

/// <summary>无界面的启动/退出总入口。取消等待不取消后台资源回收，重复退出共享同一清理任务。</summary>
public interface IApplicationLifecycle
{
    ApplicationRunState State { get; }
    void Register(ApplicationLifecycleStep step);
    Task StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

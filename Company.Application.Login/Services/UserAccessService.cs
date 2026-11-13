using Company.Application.Share.Login;
using Company.Core.Ioc;
using System.ComponentModel;
namespace Company.Application.Login.Services;

/// <summary>
/// 从当前会话统一计算页面访问、模式控制与参数编辑权限，并发布身份变化通知。
/// 身份、维护锁或调试资格变化时递增权限版本，供正在排队的操作识别授权是否已经失效。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IUserAccessService))]
public sealed class UserAccessService : IUserAccessService
{
    private readonly ISession _session;
    private long _version;
    /// <summary>监听支持属性通知的会话；通知事件保留在触发它的线程上交给订阅方处理。</summary>
    public UserAccessService(ISession session)
    {
        _session = session;
        MaintenanceInterlock.Changed += (_, _) => { Interlocked.Increment(ref _version); Changed?.Invoke(this, EventArgs.Empty); };
        DebugParameterAccess.Changed += (_, _) => { Interlocked.Increment(ref _version); Changed?.Invoke(this, EventArgs.Empty); };
        if (session is INotifyPropertyChanged observable) observable.PropertyChanged += (_, e) =>
        {
            // null/空属性名表示整体刷新；其他属性变化无需重新发布访问权限。
            if (e.PropertyName is not (nameof(ISession.CurrentUser) or null or "")) return;
            DebugParameterAccess.Revoke();
            // 使用原子计数支持跨线程读取；UI 订阅方仍需自行切回 Dispatcher。
            Interlocked.Increment(ref _version); Changed?.Invoke(this, EventArgs.Empty);
        };
    }
    // 权限定义集中于共享规则，菜单和页面可复用同一身份判定。
    public bool IsLoggedIn => UserAccessRules.IsLoggedIn(_session.CurrentUser);
    public bool CanControlModes => IsLoggedIn && !MaintenanceInterlock.Blocked;
    public bool CanEditParameters => DebugParameterAccess.CanEdit(_session.CurrentUser) && !MaintenanceInterlock.Blocked;
    public bool IsAdministrator => IsLoggedIn && _session.CurrentUser.Role == (int)UserRole.Administrator;
    public long Version => Interlocked.Read(ref _version);
    public event EventHandler? Changed;
    /// <summary>按共享规则判断当前身份是否允许访问指定导航页面。</summary>
    public bool CanOpenPage(string viewName) => UserAccessRules.CanOpenPage(_session.CurrentUser, viewName) &&
        // 维护期间轴、气缸仍可只读查看；CanEditParameters 会继续禁止设备动作和参数下发。
        !(MaintenanceInterlock.Blocked && viewName == Company.Application.Share.Prism.ViewNames.ProcessParameterView);
}

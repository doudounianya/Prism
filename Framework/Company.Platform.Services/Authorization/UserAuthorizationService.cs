using Company.Core.Ioc;
using Company.Platform.Shared.Login;
using System.ComponentModel;

namespace Company.Platform.Services.Authorization;

/// <summary>组合共享会话与应用策略，统一发布授权变化版本。没有 PLC、WPF 或 Prism 容器依赖。</summary>
[ExposedService(Lifetime.Singleton, typeof(IUserAuthorizationService))]
public sealed class UserAuthorizationService : IUserAuthorizationService, IDisposable
{
    private readonly ISession _session;
    private readonly IUserAuthorizationPolicy _policy;
    private readonly INotifyPropertyChanged? _observable;
    private long _version;
    private int _disposed;
    public UserAuthorizationService(ISession session, IUserAuthorizationPolicy policy)
    {
        _session=session;_policy=policy;_observable=session as INotifyPropertyChanged;
        _policy.Changed+=PolicyChanged;
        if(_observable is not null)_observable.PropertyChanged+=IdentityChanged;
    }
    public bool IsLoggedIn => RoleAccessRules.IsLoggedIn(_session.CurrentUser);
    public bool IsAdministrator => RoleAccessRules.IsAdministrator(_session.CurrentUser);
    public bool IsGranted(string permission) => Volatile.Read(ref _disposed)==0 && !string.IsNullOrWhiteSpace(permission) && _policy.IsGranted(_session.CurrentUser,permission);
    public long Version => Interlocked.Read(ref _version);
    public event EventHandler? Changed;
    private void Invalidate() { Interlocked.Increment(ref _version);Changed?.Invoke(this,EventArgs.Empty); }
    private void PolicyChanged(object? sender,EventArgs e) { if(Volatile.Read(ref _disposed)==0)Invalidate(); }
    private void IdentityChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is not (nameof(ISession.CurrentUser) or null or "") || Volatile.Read(ref _disposed)!=0)return;
        // 应用先撤销旧身份的设备资格，再发布新版本；保留原 PLC HMI 的通知顺序。
        _policy.OnIdentityChanged();Invalidate();
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _policy.Changed-=PolicyChanged;
        if(_observable is not null)_observable.PropertyChanged-=IdentityChanged;
    }
}

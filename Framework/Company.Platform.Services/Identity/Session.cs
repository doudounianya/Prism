using Company.Core.Ioc;
using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using System.ComponentModel;

namespace Company.Platform.Services.Identity;

/// <summary>无界面的共享会话。替换身份时同步发布标准属性通知，供授权与任意桌面框架使用。</summary>
[ExposedService(Lifetime.Singleton, typeof(ISession))]
public sealed class Session : ISession, INotifyPropertyChanged, INotifyPropertyChanging
{
    private CurrentUser _currentUser = new();
    public CurrentUser CurrentUser
    {
        get => _currentUser;
        set
        {
            // 与原 ReactiveObject 保持一致：同一对象引用不重复通知，内部字段变更不伪装成身份替换。
            if (ReferenceEquals(_currentUser, value)) return;
            PropertyChanging?.Invoke(this, new(nameof(CurrentUser)));
            _currentUser = value;
            PropertyChanged?.Invoke(this, new(nameof(CurrentUser)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event PropertyChangingEventHandler? PropertyChanging;
}

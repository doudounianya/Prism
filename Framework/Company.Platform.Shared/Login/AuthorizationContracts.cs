using Company.Platform.Shared.Model;

namespace Company.Platform.Shared.Login;

/// <summary>公共权限标识。具体页面名与设备联锁由各应用自己的策略解释，未知标识默认拒绝。</summary>
public static class PlatformPermissions
{
    public const string ManageAccounts = "Accounts.Manage";
    public const string OperateEquipment = "Equipment.Operate";
    public const string ConfigureEquipment = "Equipment.Configure";
}

/// <summary>只依赖账号身份的通用角色规则；不读取 PLC、维护锁、窗口或数据库。</summary>
public static class RoleAccessRules
{
    public static bool IsLoggedIn(CurrentUser? user) => user is { Id: > 0 } &&
        !string.IsNullOrWhiteSpace(user.UserName) && Enum.IsDefined((UserRole)user.Role);
    public static bool IsAdministrator(CurrentUser? user) => IsLoggedIn(user) && user!.Role == (int)UserRole.Administrator;
    public static bool CanConfigureEquipment(CurrentUser? user) => IsLoggedIn(user) &&
        user!.Role is (int)UserRole.EquipmentEngineer or (int)UserRole.ProcessEngineer or (int)UserRole.Administrator;
    public static bool IsGranted(CurrentUser? user, string permission) => permission switch
    {
        PlatformPermissions.ManageAccounts => IsAdministrator(user),
        PlatformPermissions.OperateEquipment => IsLoggedIn(user),
        PlatformPermissions.ConfigureEquipment => CanConfigureEquipment(user),
        _ => false
    };
}

/// <summary>应用可组合角色权限与运行条件。条件变化时通知授权服务使排队中的旧权限版本失效。</summary>
public interface IUserAuthorizationPolicy
{
    bool IsGranted(CurrentUser? user, string permission);
    void OnIdentityChanged();
    event EventHandler? Changed;
}

/// <summary>所有应用通用的当前身份与授权入口，不暴露 PLC 页签、轴或模式控制概念。</summary>
public interface IUserAuthorizationService
{
    bool IsLoggedIn { get; }
    bool IsAdministrator { get; }
    bool IsGranted(string permission);
    long Version { get; }
    event EventHandler? Changed;
}

/// <summary>账号变更的应用级限制，例如 PLC HMI 的维护持锁人员保护。基本管理员规则仍由账号服务强制执行。</summary>
public interface IAccountMutationPolicy
{
    void CheckDelete(UserAccountInfo target);
    void CheckResetPassword(UserAccountInfo target);
}

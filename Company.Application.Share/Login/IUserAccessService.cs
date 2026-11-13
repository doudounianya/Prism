using Company.Application.Share.Model;
using Company.Application.Share.Prism;
namespace Company.Application.Share.Login;

/// <summary>提供当前会话的页面访问和操作能力；Changed 通知与 Version 便于界面发现授权上下文变化。</summary>
public interface IUserAccessService
{
    bool IsLoggedIn { get; }
    bool CanControlModes { get; }
    bool CanEditParameters { get; }
    bool IsAdministrator { get; }
    /// <summary>授权状态版本；需要跨异步操作校验权限的调用方应保存并在提交前比较。</summary>
    long Version { get; }
    event EventHandler? Changed;
    /// <summary>按共享导航名判断页面是否可打开；具体参数编辑或设备控制仍需检查对应能力。</summary>
    bool CanOpenPage(string viewName);
}
/// <summary>纯会话权限规则，供访问服务和业务层复用；未知角色或未知页面不会被默认放行。</summary>
public static class UserAccessRules
{
    // 登录有效性同时要求正用户编号、非空用户名和已定义角色；不依赖用户名拼写推断管理员。
    public static bool IsLoggedIn(CurrentUser? user) => user is { Id: > 0 } && !string.IsNullOrWhiteSpace(user.UserName) && Enum.IsDefined((UserRole)user.Role);
    public static bool CanEditParameters(CurrentUser? user) => IsLoggedIn(user) && user!.Role is (int)UserRole.EquipmentEngineer or (int)UserRole.ProcessEngineer or (int)UserRole.Administrator;
    // 参数设置（轴、气缸）与监控、报警、产能页公开查看；页面可访问不代表允许修改或控制。
    // 参数操作仍通过 CanEditParameters 校验角色、真实手动调试状态及维护联锁。
    public static bool CanOpenPage(CurrentUser? user, string viewName) => viewName switch
    {
        ViewNames.DeviceMonitorView or ViewNames.RealtimeAlarmView or ViewNames.AlarmHistoryView or ViewNames.AlarmAnalysisView or
        ViewNames.ProductionTodayView or ViewNames.ProductionDailyView or ViewNames.ProductionTrendView or
        ViewNames.AxisParameterView or ViewNames.CylinderParameterView => true,
        ViewNames.ModeControlView or ViewNames.IoMonitorView or ViewNames.ProcessParameterView or ViewNames.OperationLogView => IsLoggedIn(user),
        ViewNames.ConfigView or ViewNames.UserManagementView => IsLoggedIn(user) && user!.Role == (int)UserRole.Administrator,
        _ => false
    };
}

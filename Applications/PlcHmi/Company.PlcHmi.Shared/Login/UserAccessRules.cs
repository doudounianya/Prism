using Company.Platform.Shared.Model;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.Prism;

namespace Company.PlcHmi.Shared.Login;

/// <summary>纯会话权限规则，供访问服务和业务层复用；未知角色或未知页面不会被默认放行。</summary>
public static class UserAccessRules
{
    // 登录有效性同时要求正用户编号、非空用户名和已定义角色；不依赖用户名拼写推断管理员。
    public static bool IsLoggedIn(CurrentUser? user) => RoleAccessRules.IsLoggedIn(user);
    public static bool CanEditParameters(CurrentUser? user) => RoleAccessRules.CanConfigureEquipment(user);
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

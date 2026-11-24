using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using Company.PlcHmi.Shared.Prism;

namespace Company.PlcHmi.Shared.Login;

/// <summary>本应用的页面标识，公共平台不认识这些导航名称。</summary>
public static class PlcHmiPermissions
{
    public const string PagePrefix="PlcHmi.Page:";
    public static string Page(string viewName)=>PagePrefix+viewName;
}

/// <summary>将通用角色与原 PLC 调试/维护条件组合。所有旧条件、公开页面范围及身份切换撤销顺序保持不变。</summary>
public sealed class PlcHmiAuthorizationPolicy : IUserAuthorizationPolicy,IDisposable
{
    private int _disposed;
    public PlcHmiAuthorizationPolicy()
    {
        MaintenanceInterlock.Changed+=ConditionsChanged;
        DebugParameterAccess.Changed+=ConditionsChanged;
    }
    public bool IsGranted(CurrentUser? user,string permission)
    {
        if(Volatile.Read(ref _disposed)!=0)return false;
        if(permission.StartsWith(PlcHmiPermissions.PagePrefix,StringComparison.Ordinal))
        {
            var page=permission[PlcHmiPermissions.PagePrefix.Length..];
            return UserAccessRules.CanOpenPage(user,page)&&!(MaintenanceInterlock.Blocked&&page==ViewNames.ProcessParameterView);
        }
        return permission switch
        {
            PlatformPermissions.OperateEquipment=>RoleAccessRules.IsLoggedIn(user)&&!MaintenanceInterlock.Blocked,
            PlatformPermissions.ConfigureEquipment=>DebugParameterAccess.CanEdit(user)&&!MaintenanceInterlock.Blocked,
            _=>RoleAccessRules.IsGranted(user,permission)
        };
    }
    public void OnIdentityChanged()=>DebugParameterAccess.Revoke();
    public event EventHandler? Changed;
    private void ConditionsChanged(object? sender,EventArgs e) { if(Volatile.Read(ref _disposed)==0)Changed?.Invoke(this,EventArgs.Empty); }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        MaintenanceInterlock.Changed-=ConditionsChanged;
        DebugParameterAccess.Changed-=ConditionsChanged;
    }
}

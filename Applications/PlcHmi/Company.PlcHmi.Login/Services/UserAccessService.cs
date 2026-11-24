using Company.Core.Ioc;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Shared.Login;

namespace Company.PlcHmi.Login.Services;

/// <summary>现有 HMI 页面接口的薄适配器。身份、权限版本和通知由公共服务统一维护，不重复保存会话状态。</summary>
[ExposedService(Lifetime.Singleton,typeof(IUserAccessService))]
public sealed class UserAccessService(IUserAuthorizationService authorization) : IUserAccessService
{
    public bool IsLoggedIn=>authorization.IsLoggedIn;
    public bool IsAdministrator=>authorization.IsAdministrator;
    public bool CanControlModes=>authorization.IsGranted(PlatformPermissions.OperateEquipment);
    public bool CanEditParameters=>authorization.IsGranted(PlatformPermissions.ConfigureEquipment);
    public bool CanOpenPage(string viewName)=>authorization.IsGranted(PlcHmiPermissions.Page(viewName));
    public long Version=>authorization.Version;
    public event EventHandler? Changed { add=>authorization.Changed+=value;remove=>authorization.Changed-=value; }
}

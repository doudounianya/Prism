using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;

namespace Company.Platform.Services.Authorization;

/// <summary>只有公共角色权限的应用可直接使用；设备模式或人员锁由应用提供更具体的策略。</summary>
public sealed class RoleAuthorizationPolicy : IUserAuthorizationPolicy
{
    public bool IsGranted(CurrentUser? user,string permission)=>RoleAccessRules.IsGranted(user,permission);
    public void OnIdentityChanged() { }
    public event EventHandler? Changed { add { } remove { } }
}

/// <summary>没有额外设备限制时可显式选用；管理员、自删、最后管理员与原密码校验并未被取消。</summary>
public sealed class NoAdditionalAccountMutationPolicy : IAccountMutationPolicy
{
    public void CheckDelete(UserAccountInfo target) { }
    public void CheckResetPassword(UserAccountInfo target) { }
}

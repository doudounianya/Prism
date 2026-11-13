namespace Company.Application.Share.Login;

// 保留现有管理员 Role=1 的编号，不能按用户名授予权限。
/// <summary>持久化账户角色编号；保留既有管理员编号，显示名称与权限规则均基于此枚举。</summary>
public enum UserRole { Administrator = 1, Operator = 2, EquipmentEngineer = 3, ProcessEngineer = 4 }
/// <summary>角色选择项，Value 用于保存和权限判断，Name 用于显示。</summary>
public sealed record UserRoleChoice(UserRole Value, string Name);
/// <summary>集中维护角色显示顺序和中文名称，避免各页面重复定义角色映射。</summary>
public static class UserRoles
{
    public static IReadOnlyList<UserRoleChoice> Choices { get; } = Array.AsReadOnly(new[]
    {
        new UserRoleChoice(UserRole.Operator, "操作员"), new(UserRole.EquipmentEngineer, "设备工程师"),
        new(UserRole.ProcessEngineer, "工艺工程师"), new(UserRole.Administrator, "管理员")
    });
    public static string Name(int role) => Choices.FirstOrDefault(r => (int)r.Value == role)?.Name ?? "未配置角色";
}
/// <summary>不含密码的账户查询结果；Role 保留持久化编号，RoleName 由共享角色表解释。</summary>
public sealed record UserAccountInfo(int Id, string UserName, int Role, DateTime CreatedAt)
{
    public string RoleName => UserRoles.Name(Role);
}
/// <summary>账户认证与维护契约；密码及确认值仅作为操作输入，查询结果不暴露密码字段。</summary>
public interface IUserAccountService
{
    /// <summary>认证成功返回账户信息，失败返回空；实现负责凭据验证，调用方再建立登录会话。</summary>
    Task<UserAccountInfo?> AuthenticateAsync(string userName, string password);
    Task<IReadOnlyList<UserAccountInfo>> GetUsersAsync();
    Task CreateUserAsync(string userName, string password, string confirmation, UserRole role);
    Task DeleteUserAsync(int userId);
    /// <summary>为指定账户重设密码；与用户提供旧密码的自行改密流程区分。</summary>
    Task ResetPasswordAsync(int userId, string password, string confirmation);
    Task ChangePasswordAsync(string userName, string oldPassword, string newPassword, string confirmation);
}

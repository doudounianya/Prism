using System.Diagnostics.CodeAnalysis;

namespace Company.Database.Accounts;

/// <summary>
/// 账户存储契约，只描述当前需要的查询和修改，不暴露 ORM 查询对象或通用全表操作。
/// 认证、角色授权、维护锁和密码计算由上层服务负责；数据库故障继续向调用方报告。
/// </summary>
public interface IUserAccountRepository
{
    UserAccount? FindById(int id);
    IReadOnlyList<UserAccount> List();
    bool TryAuthenticate(string userName, string passwordHash, [NotNullWhen(true)] out UserAccount? account);
    bool Insert(UserAccount account);
    bool Update(UserAccount account);
    bool Delete(int id);
}

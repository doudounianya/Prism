using System.Diagnostics.CodeAnalysis;
using Company.Database.Accounts;
using Company.Database.SqlSugar.Infrastructure;
using Sugar = global::SqlSugar;

namespace Company.Database.SqlSugar.Accounts;

/// <summary>
/// 无状态账户仓储。每个方法创建并释放自己的数据库会话，避免登录、改密和维护验证跨线程复用客户端。
/// 构造阶段不访问数据库；完整的授权与多步骤用户管理规则仍由账户服务统一执行。
/// </summary>
public sealed class UserAccountRepository(ISqlSugarSessionFactory sessions, AccountSchema schema) : IUserAccountRepository
{
    private T Use<T>(Func<Sugar.ISqlSugarClient, T> action)
    {
        using var db = sessions.Open();
        schema.EnsureCreated(db);
        return action(db);
    }

    public UserAccount? FindById(int id) => id <= 0 ? null : Use(db => db.Queryable<UserAccount>().InSingle(id));

    public IReadOnlyList<UserAccount> List() => Use(db => db.Queryable<UserAccount>()
        .OrderByDescending(a => a.CreatedAt).OrderBy(a => a.Id).ToList());

    public bool TryAuthenticate(string userName, string passwordHash, [NotNullWhen(true)] out UserAccount? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(passwordHash)) return false;
        // 保留既有数据库用户名/摘要比较规则；值由 ORM 参数化，不拼接到 SQL。
        account = Use(db => db.Queryable<UserAccount>().Where(a => a.UserName == userName && a.PasswordHash == passwordHash).First());
        return account is not null;
    }

    public bool Insert(UserAccount account)
    {
        Validate(account);
        return Use(db =>
        {
            int id = db.Insertable(account).ExecuteReturnIdentity();
            if (id <= 0) return false;
            account.Id = id;
            return true;
        });
    }

    public bool Update(UserAccount account)
    {
        Validate(account);
        if (account.Id <= 0) return false;
        return Use(db => db.Updateable(account).IgnoreColumns(a => new { a.Id, a.CreatedAt })
            .Where(a => a.Id == account.Id).ExecuteCommand() > 0);
    }

    public bool Delete(int id) => id > 0 && Use(db => db.Deleteable<UserAccount>().Where(a => a.Id == id).ExecuteCommand() > 0);

    private static void Validate(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.UserName) || account.UserName.Length > 50) throw new ArgumentException("账户用户名长度无效。");
        if (account.PasswordHash is null || account.PasswordHash.Length != 32 || account.PasswordHash.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("账户密码摘要格式无效。");
    }
}

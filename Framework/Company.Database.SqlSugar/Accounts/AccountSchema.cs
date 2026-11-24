using Company.Database.Accounts;
using Company.Database.SqlSugar.Configuration;
using Sugar = global::SqlSugar;

namespace Company.Database.SqlSugar.Accounts;

/// <summary>
/// 首次账户操作时检查 Member 表，仅在表缺失且配置允许时创建。
/// 既有表只检查存在性，绝不在查询/登录中自动修改字段、清空数据或调整索引。
/// 检查失败不缓存成功标志，下次请求可在数据库恢复后重试。
/// </summary>
public sealed class AccountSchema(IDatabaseOptionsProvider options)
{
    private readonly object gate = new();
    private bool ready;

    public void EnsureCreated(Sugar.ISqlSugarClient session)
    {
        if (Volatile.Read(ref ready)) return;
        lock (gate)
        {
            if (ready) return;
            if (!session.DbMaintenance.IsAnyTable("Member", false))
            {
                if (!options.Get().CreateAccountTableIfMissing)
                    throw new InvalidOperationException("账户表 Member 不存在，且已禁止自动建表。请按部署说明准备数据库。");
                session.CodeFirst.InitTables<UserAccount>();
            }
            Volatile.Write(ref ready, true);
        }
    }
}

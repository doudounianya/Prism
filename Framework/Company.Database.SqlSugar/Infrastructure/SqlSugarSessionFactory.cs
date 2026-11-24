using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Company.Database.SqlSugar.Configuration;
using Sugar = global::SqlSugar;

namespace Company.Database.SqlSugar.Infrastructure;

/// <summary>调用方拥有返回的独立会话，必须用 using 释放；不同请求不共享 ORM 客户端。</summary>
public interface ISqlSugarSessionFactory
{
    Sugar.ISqlSugarClient Open();
}

/// <summary>
/// 只负责连接选项与实体映射，不包含账户、PLC 或建表逻辑。
/// 数据库连接由驱动在第一次命令执行时建立；构造工厂和创建客户端都不主动测试现场连接。
/// </summary>
public sealed class SqlSugarSessionFactory(IDatabaseOptionsProvider options) : ISqlSugarSessionFactory
{
    public Sugar.ISqlSugarClient Open()
    {
        var settings = options.Get();
        settings.Validate();
        var db = new Sugar.SqlSugarClient(new Sugar.ConnectionConfig
        {
            ConnectionString = settings.ConnectionString,
            DbType = settings.Provider switch
            {
                DatabaseProvider.MySql => Sugar.DbType.MySql,
                DatabaseProvider.SqlServer => Sugar.DbType.SqlServer,
                DatabaseProvider.Sqlite => Sugar.DbType.Sqlite,
                _ => throw new InvalidOperationException("不支持的数据库类型。")
            },
            IsAutoCloseConnection = true,
            InitKeyType = Sugar.InitKeyType.Attribute,
            ConfigureExternalServices = new Sugar.ConfigureExternalServices
            {
                EntityNameService = (type, entity) =>
                {
                    if (type.GetCustomAttribute<TableAttribute>() is { } table) entity.DbTableName = table.Name;
                },
                EntityService = (property, column) =>
                {
                    if (property.GetCustomAttribute<KeyAttribute>() is not null) column.IsPrimarykey = true;
                    if (property.GetCustomAttribute<DatabaseGeneratedAttribute>() is { } generated)
                        column.IsIdentity = generated.DatabaseGeneratedOption == DatabaseGeneratedOption.Identity;
                    if (property.GetCustomAttribute<RequiredAttribute>() is not null) column.IsNullable = false;
                    if (property.GetCustomAttribute<StringLengthAttribute>() is { } length && property.PropertyType == typeof(string)) column.Length = length.MaximumLength;
                    if (property.GetCustomAttribute<ColumnAttribute>() is { } mapped) column.DbColumnName = mapped.Name;
                }
            }
        });
        db.Ado.CommandTimeOut = settings.CommandTimeoutSeconds;
        return db;
    }
}

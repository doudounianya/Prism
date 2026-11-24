using System.Data.Common;

namespace Company.Database.SqlSugar.Configuration;

/// <summary>公共账户存储支持的数据库类型；PLC 历史 SQL 另行限定为 MySQL。</summary>
public enum DatabaseProvider { MySql, SqlServer, Sqlite }

/// <summary>应用的连接快照。连接字符串属于本机配置，不能随对象转字符串进入诊断日志。</summary>
public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.MySql;
    public string ConnectionString { get; init; } = "";
    public int CommandTimeoutSeconds { get; init; } = 30;
    public bool CreateAccountTableIfMissing { get; init; } = true;

    public void Validate()
    {
        if (!Enum.IsDefined(Provider)) throw new InvalidOperationException("不支持的数据库类型。");
        if (string.IsNullOrWhiteSpace(ConnectionString)) throw new InvalidOperationException("未配置数据库连接，请检查 Config/Database.local.json 或环境变量。");
        if (CommandTimeoutSeconds is < 1 or > 300) throw new InvalidOperationException("数据库命令超时必须为 1～300 秒。");
        try { _ = new DbConnectionStringBuilder { ConnectionString = ConnectionString }; }
        catch (ArgumentException) { throw new InvalidOperationException("数据库连接字符串格式无效，请检查本机配置。"); }
    }

    public override string ToString() => $"DatabaseOptions(Provider={Provider}, Timeout={CommandTimeoutSeconds}, ConnectionString=[已隐藏])";
}

/// <summary>延迟读取不可变连接快照；注册服务、构造仓储时无需访问数据库。</summary>
public interface IDatabaseOptionsProvider
{
    DatabaseOptions Get();
}

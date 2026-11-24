using System.Text.Json;
using System.Text.Json.Serialization;

namespace Company.Database.SqlSugar.Configuration;

/// <summary>
/// 按宿主目录读取本机连接配置。环境变量可覆盖连接及数据库类型，配置只读取一次，变更后重新启动应用。
/// 此类只读取配置文件，不创建连接、不初始化表，也不在异常消息中回显连接内容。
/// </summary>
public sealed class FileDatabaseOptionsProvider : IDatabaseOptionsProvider
{
    private readonly Lazy<DatabaseOptions> options;

    public FileDatabaseOptionsProvider() : this(
        Path.Combine(AppContext.BaseDirectory, "Config", "Database.local.json"),
        name => Environment.GetEnvironmentVariable(name)) { }

    public FileDatabaseOptionsProvider(string path, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);
        options = new(() => Load(path, environment), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public DatabaseOptions Get() => options.Value;

    private static DatabaseOptions Load(string path, Func<string, string?> environment)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        json.Converters.Add(new JsonStringEnumConverter<DatabaseProvider>(allowIntegerValues: false));
        DatabaseOptions file;
        try
        {
            file = File.Exists(path)
                ? JsonSerializer.Deserialize<DatabaseOptions>(File.ReadAllText(path), json) ?? throw new JsonException()
                : new DatabaseOptions();
        }
        catch (JsonException) { throw new InvalidOperationException("数据库本机配置格式无效，请检查 Config/Database.local.json。"); }

        string? provider = environment("PRISM_DATABASE_PROVIDER");
        if (!string.IsNullOrWhiteSpace(provider) && (!Enum.TryParse<DatabaseProvider>(provider, true, out var selected) || !Enum.IsDefined(selected)))
            throw new InvalidOperationException("数据库类型环境变量无效。");
        var result = new DatabaseOptions
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? file.Provider : Enum.Parse<DatabaseProvider>(provider, true),
            ConnectionString = environment("PRISM_DATABASE_CONNECTION_STRING") ?? file.ConnectionString,
            CommandTimeoutSeconds = file.CommandTimeoutSeconds,
            CreateAccountTableIfMissing = file.CreateAccountTableIfMissing
        };
        result.Validate();
        return result;
    }
}

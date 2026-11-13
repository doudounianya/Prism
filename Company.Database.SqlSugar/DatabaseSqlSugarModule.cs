using Company.Core.Extension;
using Company.Core.Extension.Sql;
using Company.Database;
using Company.Database.SqlSugar;
using SqlSugar;
using System.Reflection;

namespace Company.Database.SqlSugar
{
    public class DatabaseSqlSugarModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
            // 仅注册业务仓储，不自动创建或修改历史表。
            containerRegistry.RegisterSingleton<PLC.PlcHistoryDbSessionFactory>();
            if (!containerRegistry.IsRegistered<Company.Database.PLC.IProductionHistoryQueryRepository>())
                containerRegistry.RegisterSingleton<Company.Database.PLC.IProductionHistoryQueryRepository, PLC.ProductionHistoryQueryRepository>();
            if (!containerRegistry.IsRegistered<Company.Database.PLC.IAlarmHistoryRepository>())
                containerRegistry.RegisterSingleton<Company.Database.PLC.IAlarmHistoryRepository, PLC.AlarmHistoryRepository>();
            if (!containerRegistry.IsRegistered<Company.Database.PLC.IProductionHistoryRepository>())
                containerRegistry.RegisterSingleton<Company.Database.PLC.IProductionHistoryRepository, PLC.ProductionHistoryRepository>();

            // 1. 注册 ISqlSugarClient 单例（适配低版本 SqlSugar 2个参数）
            if (!containerRegistry.IsRegistered<ISqlSugarClient>())
            {
                containerRegistry.RegisterSingleton<ISqlSugarClient>(() =>
                {
                    var connectionConfig = new ConnectionConfig
                    {
                        ConnectionString = LoadLocalConnectionString(),
                        DbType = DbType.MySql,
                        IsAutoCloseConnection = true,
                        InitKeyType = InitKeyType.Attribute,
                        // 核心修改：适配 2 个参数的 EntityService 委托
                        ConfigureExternalServices = new ConfigureExternalServices
                        {
                            // 低版本 SqlSugar 委托签名：Action<PropertyInfo, EntityColumnInfo>
                            EntityService = (propertyInfo, columnInfo) =>
                            {
                                // ========== 解析自定义主键特性（PrimaryKeyAttribute） ==========
                                var primaryKeyAttr = propertyInfo.GetCustomAttribute<PrimaryKeyAttribute>();
                                if (primaryKeyAttr != null)
                                {
                                    columnInfo.IsPrimarykey = true; // SqlSugar 主键配置
                                    columnInfo.IsIdentity = primaryKeyAttr.IsIdentity; // 自增配置
                                }

                                // ========== 解析自定义非空特性（RequiredAttribute） ==========
                                var requiredAttr = propertyInfo.GetCustomAttribute<RequiredAttribute>();
                                if (requiredAttr != null)
                                {
                                    columnInfo.IsNullable = false; // SqlSugar 非空配置（NOT NULL）
                                }

                                //// ========== 解析自定义唯一特性（UniqueAttribute） ==========
                                //var uniqueAttr = propertyInfo.GetCustomAttribute<UniqueAttribute>();
                                //if (uniqueAttr != null)
                                //{
                                //    columnInfo.IsUnique = true; // SqlSugar 唯一配置（UNIQUE 索引）
                                //}

                                // ========== 解析自定义字符串长度特性（StringLengthAttribute） ==========
                                var stringLengthAttr = propertyInfo.GetCustomAttribute<StringLengthAttribute>();
                                if (stringLengthAttr != null && propertyInfo.PropertyType == typeof(string))
                                {
                                    columnInfo.Length = stringLengthAttr.MaxLength; // SqlSugar 字段长度配置
                                }

                                // ========== 兼容实体类型判断（替代原 entityInfo.Type） ==========
                                // 通过 PropertyInfo.DeclaringType 获取当前字段所属的实体类型
                                Type entityType = propertyInfo.DeclaringType;
                                // 若需针对特定实体额外配置（可选，当前无需修改，保持兼容）
                                if (entityType == typeof(Member))
                                {
                                    // 可添加额外的实体专属配置（当前无需，特性解析已覆盖）
                                }
                            }
                        }
                    };
                    return new SqlSugarClient(connectionConfig);
                });
            }

            // 2. 注册 IMemberRepository 单例（无需修改，保持原有逻辑）
            if (!containerRegistry.IsRegistered<IMemberRepository>())
            {
                containerRegistry.RegisterSingleton<IMemberRepository, MemberRepository>();
            }
            if (!containerRegistry.IsRegistered<IAlarmInfoRepository>())
            {
                containerRegistry.RegisterSingleton<IAlarmInfoRepository, AlarmInfoRepository>();
            }
        }
        /// <summary>仓库不包含数据库凭据；从进程环境或本机忽略文件读取，保持原仓储及SqlSugar注册结构。</summary>
        private static string LoadLocalConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("CRT_HMI_DB_CONNECTION");
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "database.connection.local.txt");
            if (System.IO.File.Exists(path))
            {
                value = System.IO.File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            throw new InvalidOperationException("请设置 CRT_HMI_DB_CONNECTION，或在程序 Config/database.connection.local.txt 中配置本机数据库连接。该文件不得提交。 ");
        }

    }
}

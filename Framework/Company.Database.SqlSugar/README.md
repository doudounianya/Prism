# SqlSugar 数据库适配器

该项目实现公共账户存储与数据库会话管理，目标为 net8.0，不依赖 Prism/WPF。连接、映射、建表策略与仓储分开维护。`DatabaseSqlSugarModule` 的注册入口已移到应用的 Company.PlcHmi.Composition，模块标识和注册行为继续兼容。

| 目录 | 职责 |
| --- | --- |
| `Configuration` | 读取本机连接选项，处理环境变量覆盖，验证参数并隐藏日志中的连接内容 |
| `Infrastructure` | 创建独立、由调用方释放的 SqlSugar 会话；解释标准 .NET 表/列映射特性 |
| `Accounts` | 账户表准备与账户仓储；每个操作使用独立会话 |
| `Schema` | 可选人工维护脚本；不会在启动或查询时自动执行 |

账户仓储支持 MySQL、SQL Server 和 SQLite；本次离线集成验证使用临时 SQLite，MySQL/SQL Server 做了实体映射及参数化 SQL 生成验证。实际现场 MySQL 没有被测试程序连接。PLC 历史模块包含 MySQL 专用 SQL，不能通过修改这个配置使其自动适配其他数据库。

## 本机配置

应用读取宿主输出目录的 `Config/Database.local.json`，示例格式见 `Database.example.json`。真实连接值已从旧模块迁出，分别保留到本机 Debug/Release 输出和相应 `ConfigurationProfiles`，没有写入新的 C# 代码。

- `PRISM_DATABASE_CONNECTION_STRING` 可以覆盖文件中的连接字符串。
- `PRISM_DATABASE_PROVIDER` 可以覆盖类型，支持 `MySql`、`SqlServer`、`Sqlite`。
- 账户命令超时默认为 30 秒；PLC 历史工作会话保持原 5 秒超时。
- 配置延迟读取并缓存，修改后重新启动软件。
- `Database.local.json` 是包含凭据的本机文件，已加入 `.gitignore`；部署模板只提供无真实凭据的示例。

## 数据兼容与初始化

已有 `Member` 表只检查存在性，登录和查询不会自动执行 `ALTER TABLE`。当表缺失且 `CreateAccountTableIfMissing=true` 时，仅创建账户表；禁止自动创建时会明确报错。失败不会缓存为已准备完成，下次可在数据库恢复后重试。

用户名重名、管理员角色、本人密码验证及维护锁限制继续由账户服务执行。`Schema/AccountUserNameIndex.MySql.sql` 提供可选的数据库唯一索引维护步骤，需要先检查既有重名数据；本次没有在现场执行。密码摘要仍保留旧 32 位格式，改用新算法应另做字段与账户兼容迁移。

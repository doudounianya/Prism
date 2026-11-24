# 数据库存储契约

本项目是独立的 .NET 8 类库，只保留公共账户存储模型和仓储契约，不依赖 Prism、WPF、ReactiveUI、SqlSugar、日志库或其他工程。

- `Accounts/UserAccount.cs`：账户存储记录，显式映射到原 `Member` 表。
- `Accounts/IUserAccountRepository.cs`：按编号查询、列表、摘要认证及账户增删改。

权限判断、密码计算、本人改密和维护锁属于应用服务；仓储负责参数化读写和资源释放。仓储接口不返回 ORM 查询对象。密码摘要只供账户服务使用，用户列表继续通过不含摘要的 `UserAccountInfo` 返回。

| C# 属性 | 既有数据库列 |
| --- | --- |
| `Id` | `Id`，自增主键 |
| `UserName` | `UserName`，最长 50 字符 |
| `PasswordHash` | `Password`，保留现有 32 位摘要 |
| `Role` | `Role`，保留角色编号 |
| `CreatedAt` | `InsertDate`，保持原时间语义 |

PLC 报警、产能、观察片段和检查点不是公共账户库职责，已移至 `Applications/PlcHmi/Company.PlcHmi.Shared/Persistence`。实现与 SQL 脚本位于 `Company.PlcHmi.Persistence`。

原 `EntityBase`、通用 `IRepository`、`Member` 仓储及未被应用使用的 `AlarmInfo` 示例不再作为活动代码保留。原文件可从本次重构前的源码备份恢复；既有数据库表没有删除或转换。

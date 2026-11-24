# 数据库重构记录

日期：2026-11-17。重构前备份：`F:\Myself\Project\Enovix\04_程序与配置\03_上位机软件\CRT_HMI_Backups\database-refactor-20261117-191221`。

公共账户契约位于 `Framework/Company.Database`，SqlSugar 连接与账户实现位于 `Framework/Company.Database.SqlSugar`。PLC 历史数据契约迁入 `Company.PlcHmi.Shared/Persistence`，存储实现和原 SQL 迁入新模块 `Company.PlcHmi.Persistence`。保持既有 Prism 模块发现、导航和硬件架构。

账户记录从 Member 重命名为 UserAccount，显式保持数据库表 Member 及 Password、InsertDate 列。旧通用 CRUD 和未使用的 AlarmInfo 示例移出活动代码，可从备份恢复，数据库表未删除。

连接配置从模块代码迁出，保留到本机 `Config/Database.local.json` 及对应构建配置快照；环境变量可覆盖。每个请求使用独立会话并释放，构造和模块装配不访问数据库。账户表仅在缺失且配置允许时创建，既有表不自动改结构。

历史写入保留原检查点及事务协议，新增空回调校验，并在回滚失败时保留原提交异常。原历史 SQL、表结构脚本、时长及产能算法均保持不变。

离线验证：21 项目 Debug/Release 构建通过；账户、旧表兼容、权限、会话与事务验证 91/91；界面资源、角色、缓存与程序集回归 93/93；源码/配置/SQL 审计 2959/2959；两种配置的目录发现均识别 12 个模块。测试使用临时 SQLite 与事务替身，没有连接现场数据库，也没有运行 HMI 或硬件 API。MySQL/SQL Server 验证包含参数化 SQL 生成；实际现场读写仍需正常启动后联调。

可选用户名唯一索引脚本仅供人工维护，本次不执行；现有密码摘要格式保留，升级算法应另做账户数据兼容迁移。

详情见三个数据库相关项目的 README。构建、测试、审计及部署记录位于原工作区 `reviews/database-refactor-20261117`。

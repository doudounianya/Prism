# PLC HMI 历史存储

此模块承载 PLC 报警、产能及持久化日志检查点的 SqlSugar 实现，数据契约位于 `Company.PlcHmi.Shared/Persistence`。

| 目录 | 职责 |
| --- | --- |
| `Alarms` | 报警事件/片段/缺口存储，历史查询及分析 |
| `Production` | 小时产能幂等写入、来源冲突处理、生产日与趋势查询 |
| `Journal` | 独立工作会话、日志流检查点及事务提交 |
| `Schema` | 原有 MySQL 建表与核对脚本，内容保持不变，不自动执行 |

本项目为无 Prism/WPF 依赖的 net8.0 后台库。`PlcHmiPersistenceModule` 已移到 `Company.PlcHmi.Composition`，继续依赖 `DatabaseSqlSugarModule`，PLC 模块显式依赖本模块。装配入口只注册服务，解析仓储也不会建立连接；真正调用查询或提交时才创建工作会话。

公共连接工厂负责创建客户端，本模块对自己的工作会话设置 `GuidFormat=None` 及 5 秒命令超时。非 MySQL 驱动会在执行历史 SQL 前被拒绝，避免把 MySQL 专用事务语句发送给其他数据库。

变更和日志检查点仍在同一事务内提交。已提交序号不重复应用，不连续序号拒绝提交；原报警观察时长算法、产能来源冲突规则、UTC 查询边界和原 SQL 保持不变。

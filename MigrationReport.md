# PLC HMI 迁移说明

日期：2026-11-17。

原工程：`F:\Myself\Project\Enovix\04_程序与配置\03_上位机软件\CRT_HMI`。

目标工程：`F:\Myself\Code\Prism框架`。采用复制迁移，原工程保留。

迁移前备份：`F:\Myself\Project\Enovix\04_程序与配置\03_上位机软件\CRT_HMI_Backups\plchmi-migration-20261117-165219`。包含源码/资料与 Debug、Release 的配置/日志，附 SHA-256 清单。

## 命名与边界

| 原项目 | 迁移后项目 |
| --- | --- |
| `Shell` | `Applications/PlcHmi/Company.PlcHmi.Shell` |
| `Company.Application.Main` | `Applications/PlcHmi/Company.PlcHmi.Main` |
| `Company.Application.Initialize` | `Applications/PlcHmi/Company.PlcHmi.Startup` |
| `Company.Application.PLC` | `Applications/PlcHmi/Company.PlcHmi.Plc` |
| `Company.Application.Login` | `Applications/PlcHmi/Company.PlcHmi.Login` |
| `Company.Application.Menu` | `Applications/PlcHmi/Company.PlcHmi.Menu` |
| `Company.Application.Config` | `Applications/PlcHmi/Company.PlcHmi.Configuration` |
| `Company.Application.Share` | PLC 专用内容迁入 `Applications/PlcHmi/Company.PlcHmi.Shared`；通用契约提取到 `Framework/Company.Platform.Shared` |
| `Company.Core`、`Company.UI`、`Company.Logger` | 原名保留，归入 `Framework` |
| `Company.Database`、`Company.Database.SqlSugar` | 原名保留，归入 `Framework` |
| `Hardware` 下 6 个项目 | 项目名、命名空间及实现不变 |

同步修改了项目引用、应用命名空间、模块类/标识、XAML 命名空间和资源引用。原主模块类拼写 `ApplicatoinMainModule` 随迁移统一为 `PlcHmiMainModule`。模块部署集中到根目录 `Directory.Build.targets`。

本次不调整业务状态机、PLC 变量/协议、轮询周期、操作联锁、账户角色编号、配置/缓存键或数据库表结构。登录密码处理、维护锁、调试授权、历史报警时长、产能及操作日志实现保持原样。应用显示名称和单实例互斥名仍保留原设置，防止原/新程序同时启动。

## 本机状态

Debug 的 6 个配置文件与 Release 的 1 个配置文件分别保留，原日志也分别复制。两种 Excel 绑定配置存在差异，迁移没有互相覆盖。配置快照仅补充缺失的输出文件；已有配置不会被构建替换。JSON 字段名和以 `Company.Core` 枚举生成的文件名/缓存键保持不变。

## 离线验证

- 858 个原源码/资料文件、30 个原运行文件与备份哈希一致。
- 命名替换、契约拆分、项目引用、原生库、配置/日志及资料等审计共 1,848 项通过。
- 20 个项目的 Debug、Release 构建通过，0 错误。完整构建保留原有 68 条可空性/旧 SDK 声明等警告；没有在本次迁移中修改这些实现。
- 运动控制/关闭流程回归 119 项、IMC 包装层 159 项、ABI 137 项通过，共 415 项。
- 迁移专项验证 62 项通过，包括角色权限矩阵、跨程序集会话/事件、缓存 JSON、配置键和 WPF BAML/主题资源解析。
- Debug、Release 的 Prism 目录发现各识别 11 个模块；发现过程中未调用模块初始化、未加载运动卡原生库。
- 配置补种验证确认缺失文件可恢复，已存在的配置文件保持不变。

验证没有启动产品 HMI，也没有连接 PLC、机器人、运动控制卡或数据库。现场交互行为仍需要在使用新入口启动软件后核对。

验证脚本、完整映射、构建日志和部署结果位于原工作区 `reviews/plchmi-migration-20261117`。

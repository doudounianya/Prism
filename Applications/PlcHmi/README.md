# PLC HMI

打开 `PlcHmi.sln`，启动项目选择 `Company.Shell`。

| 项目 | 职责 |
| --- | --- |
| `Company.Shell` | 公共应用入口。PLC HMI 的窗口、主题、托盘、关闭流程和模块目录发现源码位于本应用的 `Company.PlcHmi.Shell` 目录 |
| `Company.PlcHmi.Composition` | 数据库、PLC 后台、历史存储和运动驱动的 Prism 装配入口 |
| `Company.PlcHmi.Startup` | 原初始化模块，编排启动、硬件关闭和界面准备 |
| `Company.PlcHmi.Main` | 设备监控、模式控制、IO、参数、报警、生产数据及操作日志 |
| `Company.PlcHmi.Persistence` | PLC 历史存储、查询和事务检查点 |
| `Company.PlcHmi.Plc` | PLC 连接、采集、重连、写入及持久化相关服务 |
| `Company.PlcHmi.Login` | 认证、账户维护、会话实现及访问服务 |
| `Company.PlcHmi.Menu` | 菜单及导航 |
| `Company.PlcHmi.Configuration` | 软件配置和系统设置 |
| `Company.PlcHmi.Shared` | PLC 契约、运行状态、历史/产能模型、导航名及 PLC 授权联锁 |

通用账户/角色、会话、授权、软件配置和事件契约位于 `../../Framework/Company.Platform.Shared`，实现位于 `Company.Platform.Services`。HMI 专用的 `IUserAccessService`、页面规则、维护与调试条件位于本应用共享项目；Login/Services/UserAccessService 仅适配公共授权服务。PlcHmiPlatformModule 在登录模块之前注册公共单例、HMI 策略与旧密文保护用途。

维护上锁、调试授权、手动初始化按下/松开、断线重连及报警/产能/操作历史仍使用迁移前实现。没有改动现场 PLC 标签、扫描配置和角色权限条件。

`ConfigurationProfiles/Debug` 和 `ConfigurationProfiles/Release` 是各自运行配置的迁移快照；应用运行时使用宿主输出目录下的 `Config`。启动前确认使用的构建配置，避免误把另一套现场绑定当作当前绑定。

本次迁移保留原目录和压缩备份，详情见根目录 `MigrationReport.md`。

PLC 存储契约在 `Company.PlcHmi.Shared/Persistence`，实现与原有 SQL 脚本在 `Company.PlcHmi.Persistence`；公共账户数据库接口在 `Framework/Company.Database/Accounts`。重构未执行现场数据库迁移。

Shared、Plc 和 Persistence 均已移除桌面框架依赖。原 PlcHmiPlcModule、PlcHmiPersistenceModule 等类型移至 Composition，命名空间和模块名称保留。后台初始化扫描后台程序集，初始化时机和顺序保持不变。构建会将旧 Modules 目录中的七个历史装配副本迁至 RetiredModules，避免同名模块重复发现。

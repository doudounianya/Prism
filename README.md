# Prism 设备软件工程

工程包含 PLC HMI 和飞拍仿真两个应用。两者从 `Company.Shell` 构建，复用公共框架和 WPF 界面，分别由各自的应用项目提供设备数据与业务流程。

## 入口

- 在 `PrismFramework.sln` 中将 `Company.Shell` 设为启动项目。选择 `Debug` 或 `Release` 启动 PLC HMI，选择 `FlyScanDebug` 或 `FlyScanRelease` 启动飞拍仿真。
- `Applications/PlcHmi/PlcHmi.sln` 也可用于 PLC HMI 开发，启动项目同为 `Company.Shell`。
- 两种产品仍各自生成 `Company.PlcHmi.Shell.exe` 和 `Company.FlyScan.Shell.exe`，输出在各自应用原有的 `bin/Debug` 或 `bin/Release` 目录。

开发环境使用 Windows、.NET 8 Windows Desktop 和兼容的 Visual Studio/.NET SDK。

```powershell
dotnet build .\PrismFramework.sln -c Debug -m:1
dotnet build .\PrismFramework.sln -c FlyScanDebug -m:1
dotnet build .\Applications\PlcHmi\PlcHmi.sln -c Release -m:1
```

## 目录职责

| 目录 | 职责 |
| --- | --- |
| `Framework/Company.Core` | 无 WPF/Prism 依赖的配置、缓存及基础工具 |
| `Framework/Company.Desktop` | WPF 对话框、语言资源操作、Prism 注册与兼容入口 |
| `Framework/Company.UI` | 公共控件、主题、语言与转换器 |
| `Framework/Company.Hmi.Contracts`、`Framework/Company.Hmi.Presentation` | PLC HMI 与飞拍共用的页面契约、WPF 页面与控件 |
| `Framework/Company.Logger` | 日志基础设施 |
| `Framework/Company.Database`、`Company.Database.SqlSugar` | 公共账户契约、独立数据库会话与 SqlSugar 账户实现；详见各项目 README |
| `Framework/Company.Platform.Shared` | 账户、角色、会话、软件配置和事件的公共契约 |
| `Framework/Company.Platform.Services` | 无界面的账户管理、共享会话、通用授权及登录偏好服务 |
| `Hardware` | PLC 通讯、OPC UA 及运动控制卡库 |
| `Company.Shell` | 公共 WPF 入口及按产品构建 |
| `Applications/PlcHmi` | PLC HMI 装配、登录、菜单、配置、业务界面和 PLC 服务 |
| `Applications/FlyScan` | 飞拍仿真装配、示教与工艺页面、运动与时序 |
| `examples/Motion` | 飞拍轴配置、仿真配方和两轴测试示例 |
| `docs` | 源码学习手册、开发计划、历史说明和离线验收记录 |

## 模块与本机配置

模块通过原 Prism 目录发现机制加载。根目录 `Directory.Build.targets` 负责把标记了 `DeployPrismModule` 的项目部署到宿主的 `Modules`，原生库放到 `runtimes/win-x64/native`。构建验证可使用 `-p:SkipModuleDeployment=true`；未来其他应用可以通过 `PrismModuleOutputRoot` 指定自己的模块目录。

PLC HMI 可从本机的 `Applications/PlcHmi/ConfigurationProfiles` 为相应的 Debug 或 Release 输出目录补齐缺失文件。已有输出配置会保留。仓库中的 PLC 模板默认停用设备，连接端点指向本机回环。现场使用时，按实际构建配置准备设备绑定和私有数据库配置。本机配置目录由 `.gitignore` 管理。

仓库提供 [SqlSugar 数据库配置示例](Framework/Company.Database.SqlSugar/Database.example.json)、[飞拍 13 轴配置](examples/Motion/FlyScan.13Axes.json)和[双组仿真配方](examples/Motion/FlyScan.DemoRecipe.json)。以 `FlyScanDebug` 或 `FlyScanRelease` 启动 `Company.Shell` 后，可查看共用页面并使用示教配方跑双组扫描仿真。

迁移保留原配置键、缓存键、账户角色编号、数据库结构、日志及历史数据路径、单实例互斥名。可见软件名称仍由原软件配置决定。目录及命名空间调整不更改 PLC 控制协议或业务流程。

发布脚本从 `Company.Shell/Company.Shell.csproj` 按 `CompanyProduct` 分别生成两个独立安装包。直接复跑飞拍共享界面测试时，使用 `-p:CompanyProduct=FlyScan`。

数据库已按公共账户与 PLC 历史业务拆分；新增 `Applications/PlcHmi/Company.PlcHmi.Persistence` 模块，连接值保存在宿主的 `Config/Database.local.json`。

分层调整后，后台和硬件模块的 Prism 注册入口集中在 `Company.PlcHmi.Composition`；存储与 PLC 后台库保留实际实现。分层约定与校验命令见 [Architecture.md](Architecture.md)。原模块名称、配置键、权限与控制流程继续兼容。

操作请求入口与启动/退出编排见 [LifecycleOperations.md](LifecycleOperations.md)。新增离线回归：`dotnet run --project tests/Runtime.Smoke/Runtime.Smoke.csproj`，仅使用替身通讯客户端，不连接现场设备。

账户与权限已提取至公共平台。PLC HMI 在自己的装配模块中提供调试/维护策略，未来飞拍软件可独立复用公共服务。接口与装配示例见 [PlatformServices.md](PlatformServices.md)。

## 源码学习与离线验证

- 学习 Prism、WPF 和应用运行路径可从 [HTML 源码学习手册](docs/PrismFramework-Code-Guide.html) 开始。
- 阶段任务与验收范围见 [开发计划](docs/HmiFlyScanDevelopmentPlan.md)、[G2 共用页面验收](docs/G2Verification.md)和 [G4 验收记录](docs/G4Verification.md)。
- 发布候选的打包、统一离线复跑和结果见 [G6 离线验证](docs/G6ReleaseCandidates.md)。

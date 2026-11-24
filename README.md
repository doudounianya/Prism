# Prism 设备软件工程

现有 PLC HMI 已按应用边界迁入 `Applications/PlcHmi`，PLC HMI 项目统一使用 `Company.PlcHmi` 前缀。公共框架和硬件库独立存放，后续机型可以基于同一框架另建应用。

## 入口

- `PrismFramework.sln`：整个工程的入口。
- `Applications/PlcHmi/PlcHmi.sln`：PLC HMI 的开发入口，将 `Company.PlcHmi.Shell` 设为启动项目。
- 启动文件：`Applications/PlcHmi/Company.PlcHmi.Shell/bin/Debug/net8.0-windows/Company.PlcHmi.Shell.exe`。

开发环境使用 Windows、.NET 8 Windows Desktop 和兼容的 Visual Studio/.NET SDK。

```powershell
dotnet build .\PrismFramework.sln -c Debug -m:1
dotnet build .\Applications\PlcHmi\PlcHmi.sln -c Release -m:1
```

## 目录职责

| 目录 | 职责 |
| --- | --- |
| `Framework/Company.Core` | 无 WPF/Prism 依赖的配置、缓存及基础工具 |
| `Framework/Company.Desktop` | WPF 对话框、语言资源操作、Prism 注册与兼容入口 |
| `Framework/Company.UI` | 原公共控件、主题、语言与转换器 |
| `Framework/Company.Logger` | 日志基础设施 |
| `Framework/Company.Database`、`Company.Database.SqlSugar` | 公共账户契约、独立数据库会话与 SqlSugar 账户实现；详见各项目 README |
| `Framework/Company.Platform.Shared` | 账户、角色、会话、软件配置和事件的公共契约 |
| `Framework/Company.Platform.Services` | 无界面的账户管理、共享会话、通用授权及登录偏好服务 |
| `Hardware` | PLC 通讯、OPC UA 及运动控制卡库 |
| `Applications/PlcHmi` | PLC HMI 宿主、启动、登录、菜单、配置、业务界面和 PLC 服务 |
| `Applications/FlyScan` | 飞拍仿真宿主、示教配方、C# 时序和本地历史 |
| `docs` | 开发计划与离线验收记录 |

## 模块与本机配置

模块通过原 Prism 目录发现机制加载。根目录 `Directory.Build.targets` 负责把标记了 `DeployPrismModule` 的项目部署到宿主的 `Modules`，原生库放到 `runtimes/win-x64/native`。构建验证可使用 `-p:SkipModuleDeployment=true`；未来其他应用可以通过 `PrismModuleOutputRoot` 指定自己的模块目录。

PLC HMI 的 Debug 和 Release 现场配置各自保存在 `Applications/PlcHmi/ConfigurationProfiles`。首次构建补充缺失配置文件，已有输出配置不会被覆盖。两种配置的原始内容不同，不能互相替换；请按实际使用的构建配置启动。此目录包含本机登录缓存、维护状态和现场绑定，已列入 `.gitignore`，是本次本机迁移保留的状态。

迁移保留原配置键、缓存键、账户角色编号、数据库结构、日志及历史数据路径、单实例互斥名。可见软件名称仍由原软件配置决定。目录及命名空间调整不更改 PLC 控制协议或业务流程。

飞拍应用位于 `Applications/FlyScan`。启动 `Company.FlyScan.Shell` 后可查看共用页面，使用示教配方进行双组扫描仿真。G0 至 G4 验收见 [开发计划](docs/HmiFlyScanDevelopmentPlan.md) 和 [G4 验收记录](docs/G4Verification.md)。

数据库已按公共账户与 PLC 历史业务拆分；新增 `Applications/PlcHmi/Company.PlcHmi.Persistence` 模块，连接值保存在宿主的 `Config/Database.local.json`。

分层调整后，后台和硬件模块的 Prism 注册入口集中在 `Company.PlcHmi.Composition`；存储与 PLC 后台库保留实际实现。分层约定与校验命令见 [Architecture.md](Architecture.md)。原模块名称、配置键、权限与控制流程继续兼容。

操作请求入口与启动/退出编排见 [LifecycleOperations.md](LifecycleOperations.md)。新增离线回归：`dotnet run --project tests/Runtime.Smoke/Runtime.Smoke.csproj`，仅使用替身通讯客户端，不连接现场设备。

账户与权限已提取至公共平台。PLC HMI 在自己的装配模块中提供调试/维护策略，未来飞拍软件可独立复用公共服务。接口与装配示例见 [PlatformServices.md](PlatformServices.md)。

## 仓库配置示例

PLC 模板默认停用设备，连接端点指向回环地址。现场使用时请根据实际环境准备本机绑定文件和私有数据库配置。数据库示例位于 `Framework/Company.Database.SqlSugar/Database.example.json`。飞拍仿真可直接使用内置 13 轴和双组示例配方。

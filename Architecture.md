# 分层与依赖约定

本工程采用同一进程内的模块化应用。WPF/Prism 负责桌面界面和装配，硬件、PLC 后台、存储实现不依赖桌面运行时。两种软件经由同一 WPF 项目 `Company.Shell` 构建，各自的应用装配和桌面源码留在 `Applications` 内。

## 当前项目职责

| 层次 | 项目 | 允许依赖 |
| --- | --- | --- |
| 基础设施与契约 | Company.Core、Company.Logger、Company.Database、Company.Platform.Shared | .NET、JSON、日志库；平台事件仅使用无界面的 Prism.Events |
| 公共平台服务 | Company.Platform.Services | 基础库、公共契约和账户存储契约；不依赖应用、ORM 或桌面框架 |
| 桌面适配 | Company.Desktop、Company.UI | 基础层、WPF、Prism、桌面控件库 |
| 设备能力及驱动 | Hardware 下全部项目 | 无界面的基础设施、硬件契约和厂商 SDK |
| PLC 应用契约 | Company.PlcHmi.Shared | 平台公共契约 |
| PLC 后台 | Company.PlcHmi.Plc、Company.PlcHmi.Persistence | 应用契约、基础设施、设备能力或存储适配 |
| 应用界面 | Main、Login、Menu、Configuration、Startup | 桌面适配、应用契约、相应应用服务 |
| 应用装配 | Company.PlcHmi.Composition | 桌面注册工具、后台实现、数据库及所需硬件驱动 |
| 宿主 | Company.Shell | WPF 入口与构建时产品选择。每个产物只引用选中产品的应用项目 |

依赖方向由引用决定。基础层不得引用 Hardware 或 Applications；Hardware 不得引用 Applications 或 Company.Shell；应用项目不得反向引用 Company.Shell；共享契约不得引用应用实现；后台项目不得经直接或间接引用获得 WPF/桌面项目。

## Company.Core 与 Company.Desktop

Company.Core 保留配置、缓存、JSON/文件工具、基础模型、语言契约以及无容器依赖的服务标记元数据，目标为 net8.0。

Company.Desktop 承载对话框、语言资源操作、PrismProvider、服务注册扩展及原 CoreModule。CoreModule 现在由桌面层扫描基础程序集和桌面程序集，确保配置、缓存、语言服务继续按原单例和初始化时机注册。

本轮保留移动类型的 Company.Core 命名空间，避免顺带改动现有界面、配置键及调用方。程序集边界已经变化：使用这些桌面类型的项目必须显式引用 Company.Desktop；仅引用 Company.Core 无法获得它们。新桌面 BAML 位于 Company.Desktop。后续新增桌面 API 使用 Company.Desktop 命名空间。

## 后台与装配

数据库、PLC 和运动库中原有 IModule 已移至 Company.PlcHmi.Composition，另有 PlcHmiPlatformModule 装配公共平台服务与 HMI 策略。PLC 注册仍扫描真实后台程序集，不能扫描装配程序集冒充后台注册。Composition 由同一个 DirectoryModuleCatalog 发现，其中包含八个 IModule；另五个界面模块仍独立部署。CoreModule 由 Shell 显式加入目录。

MotionControllerFactory 接收解析函数，保持每个驱动单例、延迟创建、仅关闭已解析驱动的行为。Prism 命名注册由 Composition 提供；驱动库不认识容器。SDK、运动单位、轴占用、轨迹完成判断及异常处理均保留原实现。各驱动仍限定 Windows，但不引用 WindowsDesktop。

现场 Modules 必须同步更新，旧数据库/PLC/运动模块 DLL 不能与新装配模块并存。正常重新编译已有旧输出时，构建会移除这些明确列出的旧模块副本；后台 DLL 在宿主根目录提供给装配模块使用。

## 本轮保留的应用边界

PLC 的采集、重连、权限联锁、维护锁、报警和产能处理仍位于原应用。账户、会话、角色授权和登录偏好实现已提取至 Company.Platform.Services；PLC 条件通过应用策略注入。未新增工作流引擎或高级运动功能，密码摘要和数据库结构保持兼容。

操作请求和后台生命周期已统一：Company.Core.Runtime 提供无界面协调器；应用各服务在模块加载阶段登记启动与退出步骤。IPlcWriteService 的原便捷 API 统一委托 ExecuteAsync，由 PlcOperationPolicy 在入队及真正发送前复核权限、模式和原连接。退出先关新操作入口，处理本入口持有的原按住/调试请求，再停止 PLC、历史保存及可选运动卡。详见 [LifecycleOperations.md](LifecycleOperations.md)。

现有 PrismProvider 静态入口仅留在桌面层，供已有界面兼容使用；新后台代码通过构造参数接收依赖。现有事件语义保留。

## 校验

```powershell
python tools/verify_architecture.py
dotnet build PrismFramework.sln -c Debug -m:1
dotnet build PrismFramework.sln -c Release -m:1
dotnet build PrismFramework.sln -c FlyScanDebug -m:1
dotnet run --project tests/Headless.Smoke/Headless.Smoke.csproj
```

Directory.Build.props/targets 在编译时阻止后台项目启用 WPF/WinForms 或引用桌面包/装配项目。架构脚本进一步检查项目引用闭包、反向依赖和循环；Headless.Smoke 在普通控制台中验证仿真运动、工厂生命周期及配置兼容，不加载 WPF 和厂商 SDK。更完整的本轮回归结果见 LayeringReport.md。

# Company.Shell 迁移验证

2026-11-26 在隔离 Windows 工作区执行。PLC HMI 使用回环 OPC UA、临时 MySQL 和旧版发布包，FlyScan 使用运动卡仿真与临时数据目录。

## 开发工作区完整回归

| 检查 | 结果 |
|---|---|
| 启动项目 | `Company.Shell` 是唯一 WPF 可执行项目。PLC HMI 和 FlyScan 的产物均核对了共同入口 `Company.Shell.Program` |
| 构建、配置与目录 | PLC HMI Debug、Release 和 FlyScanDebug、FlyScanRelease 构建通过，原 PLC HMI 子解决方案仍可构建。以合成配置验证已有文件保留、缺失文件按 Debug 配置补齐 |
| 分层与警告 | 26 个项目、16 个无界面库，0 项依赖违规。PLC HMI 两种配置各 39 项既有警告，新增 0 项 |
| 发布包 | 沿用 `PlcHmi-ReleaseCandidate.zip` 和 `FlyScan-Simulation.zip`。两包来源指纹验证通过，各自校验组件和配置清单，共用界面程序集相同 |
| FlyScan | 共享页面、飞拍流程、检查点恢复及旧包升级与回退均通过 |
| PLC HMI | 历史和桌面升级回退、OPC UA 重连、轴与气缸权限、调试与初始化、维护锁及托盘和退出生命周期均通过 |

完整离线入口通过 29 项检查，0 项失败。

## 提交版复跑

以 GitHub 的 `197bf86` 为基线单独应用本次入口迁移，重新生成两种发布包。离线验证通过 22 项检查，0 项失败。复跑包含 Debug、Release 和 FlyScanDebug 构建、共享页面、共同启动入口和 PLC HMI 的历史回退、OPC UA 回环及轴与气缸参数权限。

| PLC HMI 用例 | 通过断言 |
|---|---:|
| 历史恢复与回退 | 58 |
| OPC UA 回环 | 46 |
| 轴与气缸参数权限 | 210 |

在 Windows 上准备 .NET SDK、本地 NuGet 缓存、MySQL Server 8 和迁移前的 PLC HMI 候选包。使用新的输出目录。

```powershell
$nugetSource = Join-Path $env:USERPROFILE '.nuget/packages'
python tools/package_release_candidates.py --restore-source $nugetSource --output C:/Temp/PrismShellCandidates
python tools/verify_release_candidates.py --packages C:/Temp/PrismShellCandidates --output C:/Temp/PrismShellVerification --restore-source $nugetSource --desktop --plc-history-baseline C:/Temp/PreviousPrismPackages/PlcHmi-ReleaseCandidate.zip --plc-opcua --plc-parameters --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

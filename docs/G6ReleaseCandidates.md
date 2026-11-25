# PLC HMI 与飞拍仿真发布候选

## 生成与核对

在 Windows x64 上用 .NET SDK 和 Python 3.11 或更高版本从工程根目录生成两个独立的 ZIP。运行时需要安装 x64 .NET 8 Windows Desktop Runtime。两个包各自解压到新目录即可安装，升级时保留上一版解压目录。

```powershell
python tools/package_release_candidates.py --output C:/Temp/PrismCandidates
```

目标目录须为新目录。离线构建可加 `--restore-source <本机 NuGet 包目录>`。两个产品均采用框架依赖型发布。每个 ZIP 自带 `package-manifest.json`，登记宿主、构建配置、运行时及每个文件的 SHA-256。

| 安装包 | 启动程序 | 随包配置 |
| --- | --- | --- |
| `PlcHmi-ReleaseCandidate.zip` | `Company.PlcHmi.Shell.exe` | 数据库配置示例。PLC 绑定工作簿和数据库连接随目标设备配置恢复 |
| `FlyScan-Simulation.zip` | `Company.FlyScan.Shell.exe` | 仿真的 13 轴和示例配方。包中使用仿真运动控制驱动 |

标准 PLC HMI 发布会部署六个 Prism 模块到发布目录的 `Modules`。打包时跳过本机配置种子，现场工作簿由目标设备的配置备份提供。飞拍仿真包的文件清单会检查 PLC HMI 程序集、汇川驱动和原生运动卡库，发现混入时中止打包。

## 重跑离线验收

在工程根目录运行统一入口，验收结果目录须为新目录。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismVerification
```

入口按清单复核两个现有 ZIP 的路径、文件哈希、模块隔离和公共界面程序集一致性，再在临时源码副本上运行分层检查、Debug 与 Release 构建、运行时及权限回归、13 轴配置、飞拍流程和 H0 离线测试。使用 `--packages-only` 可只核对 ZIP。交互式 Windows 会话可加 `--desktop` 运行源码构建的飞拍界面和共享 IO 仿真。离线 NuGet 恢复沿用 `--restore-source`。

`report.json` 分别记录安装包哈希、源码快照哈希、各项通过和失败、未运行项及日志位置。任一已选检查失败时返回退出码 `1`。PLC HMI 桌面和回退验收仍使用隔离数据库及 OPC UA 夹具，H0 实机验收仍为待验证。

本轮离线复跑的 17 项检查均通过。两份重建包的清单核验和损坏包的失败用例已保留在 [G6 离线复跑证据](verification/2026-11-25-g6-replay.zip)。

## 配置和数据恢复

1. PLC HMI 退出后，备份旧安装的 `Config` 和该实例使用的数据库。新安装先还原对应的 `Config/PLC/CRT_HMI_PLC变量绑定配置.xlsx` 和 `Config/Database.local.json`。做发布验收时把数据库备份还原为独立测试实例，连接串和设备绑定均指向测试环境。回退时退出候选版本，从保留的旧安装和对应备份启动。本次候选沿用 G4 数据结构。
2. 飞拍仿真退出后，备份 `%LOCALAPPDATA%\Company\FlyScan\Simulation` 及保存到其他目录的配方。包含待恢复任务时，`run.json` 与 `history.sqlite` 使用同时点的备份。恢复后核对任务 ID、扫描组和完成件计数。

## 放行状态

PLC HMI 和飞拍仿真各自维护离线验收结论。飞拍生产版继续等待 H0 实机验收、工艺限值确认和 G5，当前只提供仿真安装包。

## 2026-11-25 离线验收记录

候选以提交 `7becd718119caa3ace11790e8aa30eeba53b09e8` 为基线，叠加本轮发布装配和打包脚本。整套解决方案的 Debug、Release 构建均为 0 错误。依赖审计覆盖 27 个项目和 16 个无界面库，0 项违规。

| 检查 | 实测结果 |
| --- | --- |
| 安装包与隔离 | PLC HMI 的 137 个文件、飞拍仿真的 50 个文件均按清单核对 SHA-256。两个包的公共界面 DLL 哈希一致。PLC HMI 发布目录含 6 个动态模块，飞拍仿真包无 PLC HMI 或汇川驱动 |
| PLC HMI 升级与回退 | 从测试库副本依次运行 G4、解包后的候选、回退 G4，各通过 88 项。三轮均通过登录、IO 重连、模式及权限检查、既有报警查询，日报及趋势均读取 58 件 |
| 飞拍仿真 | 解包目录中的界面 59 项、IO 13 项、飞拍流程 61 项通过。从检查点和一致的 SQLite 快照恢复到另一份解包目录，保持同一工件 ID，最终结算 1 件、2 组、6 次触发 |
| 旧模块升级及回归 | 旧汇川模块副本按原哈希迁到 `RetiredModules`。发布模式跳过本机配置种子。Headless 冒烟、运行时 65 项、HMI 权限 118 项、13 轴配置 54 项及 H0 离线核验 9 项通过 |

PLC HMI 验收由托管工装在解包目录启动真实 Prism 宿主，只连接回环 OPC UA 和 MySQL 测试库副本。飞拍验收使用独立数据目录。候选未部署到现场。包哈希及复验日志见 [G6 离线证据](verification/2026-11-25-g6-offline.zip)。

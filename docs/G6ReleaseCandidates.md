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

`report.json` 分别记录安装包哈希、源码快照哈希、各项通过和失败、未运行项及日志位置。任一已选检查失败时返回退出码 `1`。PLC HMI 的历史回退和 OPC UA 回环均可按需复跑。H0 实机验收仍为待验证。

### PLC HMI 历史恢复与回退

在交互式 Windows 会话中，使用同一入口传入上一版自有 PLC HMI 包和本机 MySQL Server 8 的 `bin` 目录。Python 环境需要 `openpyxl` 用于生成测试工作簿。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismVerification --plc-history-baseline C:/Temp/Previous/PlcHmi-ReleaseCandidate.zip --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

工装在独立临时目录初始化只监听 `127.0.0.1` 的 MySQL 实例。校验和解包后，按旧包、候选包、恢复备份后回退旧包的顺序启动 PLC HMI。旧包首次运行生成合成测试账号，候选和回退阶段均用原账号重新登录。三轮核对同一条报警及 60 秒有效观测时长，并核对生产日报总量 58 件和趋势的白班 41 件、夜班 17 件。回退使用单独恢复的测试库。测试结束关闭 MySQL 实例。

`plc-history/report.json` 记录三个阶段各自使用的包哈希、检查数、日志和 SQL 备份哈希。工作簿中的 PLC 连接全部禁用，三轮确认实际加载了这份配置。该用例覆盖打包后的登录、只读状态、历史查询、回退和退出。

2026-11-25 用原 G6 包和重新构建的候选包完成演练。旧包、候选、回退三轮分别通过 20、19、19 项断言。统一入口的 19 项检查全部通过。原始 SQL 备份随本次临时环境清理，证据保留备份哈希和实际查询结果。 [PLC HMI 历史恢复与回退证据](verification/2026-11-25-plc-history-rollback.zip)

### PLC HMI OPC UA 回环

在交互式 Windows 会话中，为统一入口传入 `--plc-opcua` 与本机 MySQL Server 8 的 `bin` 目录。可同时加 `--plc-history-baseline` 复跑历史回退。两项使用独立临时配置和 MySQL 实例，Python 环境沿用 `openpyxl`。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismOpcUa --plc-opcua --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

工装校验并解包 PLC HMI 候选包，生成只启用一个回环设备的测试工作簿，核对唯一可写绑定为 `b_Action[3]`。OPC UA 测试服务使用 `127.0.0.1` 上的临时端口，并检查实际监听地址。用例依次验证以下行为。

- 实际模式和八种状态反馈更新模式图。稳定反馈保留人工选中的页签，模式变化时保留当前顶层页面。
- 用界面停止按钮的按住和释放键事件发出 `True`、`False`。服务端分别记录一次写入，界面读回两次结果。重复键事件不多发，邻位保持原值。
- DI 和 DO 页面只读。断线时当前 IO 变为未知。恢复后连接代次递增，页面显示新会话的 DI 值，测试服务未收到旧停止握手的重放。

总报告的 `plcOpcUaLoopback` 记录本项结果，`plcDesktopAndRollback` 继续记录全量桌面矩阵的独立夹具要求。`plc-opcua/report.json` 记录候选包哈希、断言数量和详细日志。测试在专用回环服务结束后关闭临时 MySQL。

2026-11-25 使用重新构建的候选包完成回环复跑，44 项断言通过。统一入口合并历史回退、飞拍界面和流程后共 20 项检查通过。 [OPC UA 回环验证记录](verification/2026-11-25-plc-opcua-loopback.zip)

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

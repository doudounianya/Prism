# PLC HMI 与飞拍仿真发布候选

## 生成与核对

在 Windows x64 上用 .NET SDK 和 Python 3.11 或更高版本从工程根目录生成两个独立的 ZIP。运行时需要安装 x64 .NET 8 Windows Desktop Runtime。两个包各自解压到新目录即可安装，升级时保留上一版解压目录。

```powershell
python tools/package_release_candidates.py --output C:/Temp/PrismCandidates
```

目标目录须为新目录。离线构建可加 `--restore-source <本机 NuGet 包目录>`。两个产品均从同一份干净源码副本构建，采用框架依赖型发布。每个 ZIP 自带 `package-manifest.json`，登记宿主、构建配置、运行时、发布输入指纹及每个文件的 SHA-256。

| 安装包 | 启动程序 | 随包配置 |
| --- | --- | --- |
| `PlcHmi-ReleaseCandidate.zip` | `Company.PlcHmi.Shell.exe` | 数据库配置示例。PLC 绑定工作簿和数据库连接随目标设备配置恢复 |
| `FlyScan-Simulation.zip` | `Company.FlyScan.Shell.exe` | 仿真的 13 轴和示例配方。包中使用仿真运动控制驱动 |

标准 PLC HMI 发布会部署六个 Prism 模块到发布目录的 `Modules`。打包时跳过本机配置种子，现场工作簿由目标设备的配置备份提供。飞拍仿真包的文件清单会检查 PLC HMI 程序集、汇川驱动和原生运动卡库，发现混入时中止打包。

## 重跑离线验收

已核验候选包的 SHA-256、实测结果和原始证据见 [本版候选验收结论](HmiFlyScanAcceptance.md)。

在交互式 Windows 会话中，从工程根目录运行完整离线矩阵。使用上节命令生成与当前工程对应的两份候选 ZIP，把两份上一版 ZIP 放在基线目录。验收结果使用新目录。

```powershell
python tools/verify_release_candidates.py --full-offline --packages C:/Temp/PrismCandidates --baseline-packages C:/Temp/Previous --output C:/Temp/PrismVerification --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

完整模式包含包清单、源码构建、分层检查、H0 离线测试、飞拍仿真界面和升级回退，以及 PLC HMI 历史回退、桌面回退、OPC UA 回环、参数权限、模式操作和生命周期。PLC 验证使用本机回环和临时 MySQL。需要不同的回退基线时，可分别指定 `--plc-history-baseline`、`--plc-desktop-baseline` 和 `--flyscan-baseline`。离线 NuGet 恢复使用 `--restore-source`。

`report.json` 记录安装包和源码快照的哈希、检查结果及日志位置。完整模式中任一必测项缺失、跳过或失败，退出码为 `1`。完整离线结论见 `separateAcceptance.fullOffline`。缺少基线或 MySQL 目录时在运行前报错。实机验收状态单独记录在 `separateAcceptance.h0HardwareBench`。

`package-source-inputs` 核对两个候选包的发布输入指纹是否与本次复跑一致。指纹覆盖应用、框架、硬件、随包示例及解决方案和构建配置。验证工具、测试源码另计入完整源码快照。候选包在复跑开始时复制到隔离目录，各项安装验证使用同一对包。已有旧包仍可作升级与回退基线，也可用 `--packages-only` 核对清单。

2026-11-25 从同一份源码生成两套新候选包，462 个发布输入逐项纳入指纹。完整离线复跑 28 项通过，入口工具测试 19 项通过。缺少指纹的旧候选包和源码不匹配的候选包均在构建前被拒绝。 候选包与源码验证记录（本地归档 `2026-11-25-candidate-source-gate.zip`）

正式工程使用本机绑定配置单独生成两套候选包，461 个发布输入的指纹均与正式工程复跑匹配。28 项离线检查全部通过。Git 工作副本候选包因发布输入不同，在正式工程构建前被拒绝。 正式工程候选包验证记录（本地归档 `2026-11-25-formal-candidate-source-gate.zip`）

完整复跑使用与 G0 原始日志一致的简体中文构建诊断。Debug 和 Release 分别按项目、源码位置、代码和消息对照 [G0 警告清单](../tools/g0-build-warnings.json)，结果写入 `build-warning-baseline.json`。清单记录取证归档的 SHA-256。新增诊断或警告总数与解析结果不一致均会使复跑失败。已消除的警告列在报告中。

2026-11-25 启用 G0 警告对照后，完整复跑 27 项检查全部通过。Debug 和 Release 各有 39 条既有警告，与 G0 清单逐条一致，入口工具通过 16 项测试。 警告对照与完整复跑记录（本地归档 `2026-11-25-g0-build-warning-gate.zip`）

使用 `--packages-only` 可仅核对 ZIP。选用 `--desktop` 可运行源码构建的飞拍界面与共享 IO 仿真。下文的用例均可单独复跑，完整离线验收状态在这些模式下记为未运行。

2026-11-25 首次使用 `--full-offline` 验证两套候选包，26 项离线检查全部通过。模式操作专项通过 115 项断言，入口工具通过 13 项测试。 首次完整离线验证记录（本地归档 `2026-11-25-full-offline-gate.zip`）

### 飞拍仿真发布包备份与恢复

在交互式 Windows 会话中使用 `--flyscan-recovery`。工装从通过清单校验的飞拍仿真 ZIP 展开两个独立安装目录，并使用临时数据目录。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismFlyScanRecovery --flyscan-recovery
```

- 在第一个发布目录运行界面示例，先完成一件工件，再停在第二件工件的未完成组。工装读取已保存的检查点，正常运行的测试进程在该位置结束，以覆盖进程中断后的恢复入口。
- 确认源安装退出后持有检查点锁，在临时目录采集 `run.json`、独立配方和 SQLite 快照，再打包这三份文件。恢复时在临时目录核对哈希、SQLite 完整性、工件检查点、历史记录和配方，通过后才生成目标数据目录。
- 恢复界面显示同一任务的已有配方和示教位，等待操作员确认才开始重做未完成组。
- 完成恢复后重新启动同一安装，校验历史记录保留、工件和操作日志按同一 ID 各记录一次，并核对生产趋势中两件工件的合计。同步验证原测试数据的检查点和历史保持原样。

汇总结果位于 `separateAcceptance.flyscanPackageRecovery`。详细报告记录候选包、检查点及 SQLite 备份的哈希，位于 `flyscan-package-recovery/report.json`。复跑实际使用的合成备份以 `flyscan-backup-fixture.zip` 保留在同一证据目录。

2026-11-25 完成发布包恢复复跑，三个阶段分别通过 9、10 和 5 项界面断言。恢复前累计 1 件、3 组、9 次脉冲，重启后累计 2 件、4 组、11 次脉冲。未完成组从起点重做，实际重发的脉冲按次数记录，工件按 ID 去重。统一离线复跑共 24 项检查通过。 发布包恢复验证记录（本地归档 `2026-11-25-flyscan-package-recovery.zip`）

2026-11-25 补测备份锁、文件校验失败、SQLite 完整性及恢复重试后，打包验证的 9 项用例全部通过。Windows 下已验证备份与 .NET 检查点独占锁双向互斥，恢复失败时目标目录不会出现。候选包恢复与跨版本回退均已复跑，完整离线验收通过 26 项检查。 备份与恢复完整性记录（本地归档 `2026-11-25-flyscan-backup-atomic.zip`）

### 飞拍仿真跨版本升级与回退

使用 `--flyscan-baseline` 指定上一版飞拍仿真 ZIP。统一入口分别核验旧版与候选版，验证二者的包哈希不同，回退从同一旧版 ZIP 解包。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismFlyScanRollback --flyscan-baseline C:/Temp/Previous/FlyScan-Simulation.zip
```

- 在旧版程序完成一件工件，并保存第二件部分完成的检查点。备份该时点的检查点、SQLite 快照及独立配方文件。候选版和回退版各自从这份备份恢复。
- 候选版直接显示检查点中的原配方和两组示教位。已验旧版首次回退启动会显示随包示例配方。确认恢复前应先核对配方和示教位。在隔离的旧版安装中，工装用备份配方恢复 `Config/FlyScan.DemoRecipe.json`，验证旧版实际显示相同的配方后再确认恢复。
- 两个版本分别重做同一工件的未完成组，只发出该组的两个预期触发，保留原工件和历史记录。再次启动均可查询两件工件的产量，且没有重放触发或重复结算。

汇总状态位于 `separateAcceptance.flyscanUpgradeAndRollback`。`flyscan-upgrade-and-rollback/report.json` 记录各次实际使用的安装包哈希、备份配方与数据哈希、工件 ID、回退界面探测及计数。同目录的 `flyscan-backup-fixture.zip` 是候选版与回退版共同使用的恢复来源。

2026-11-25 跨版本用例的六次启动共通过 48 项断言。候选版和回退版分别从备份的 1 件、3 组、9 次脉冲恢复至 2 件、4 组、11 次脉冲。 跨版本验证记录（本地归档 `2026-11-25-flyscan-upgrade-rollback.zip`）

2026-11-25 保留了同版本恢复与跨版本回退实际使用的两份合成备份。完整离线复跑结束并清理临时环境后，从归档重新核对两份备份的数据。在新隔离安装中使用同一份跨版本备份分别启动候选版与回退版，各通过 17 项断言。两版均保留相同的两个工件，读回 2 件、4 组、11 次脉冲。完整离线复跑 28 项通过。 两份备份、恢复日志和重放脚本（本地归档 `2026-11-25-flyscan-retained-backup.zip`）

### PLC HMI 历史恢复与回退

在交互式 Windows 会话中，使用同一入口传入上一版自有 PLC HMI 包和本机 MySQL Server 8 的 `bin` 目录。Python 环境需要 `openpyxl` 用于生成测试工作簿。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismVerification --plc-history-baseline C:/Temp/Previous/PlcHmi-ReleaseCandidate.zip --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

工装在独立临时目录初始化只监听 `127.0.0.1` 的 MySQL 实例。校验和解包后，按旧包、候选包、恢复备份后回退旧包的顺序启动 PLC HMI。旧包首次运行生成合成测试账号，候选和回退阶段均用原账号重新登录。三轮核对同一条报警及 60 秒有效观测时长，并核对生产日报总量 58 件和趋势的白班 41 件、夜班 17 件。回退使用单独恢复的测试库。测试结束关闭 MySQL 实例。

`plc-history/report.json` 记录三个阶段各自使用的包哈希、检查数、日志和 SQL 备份哈希。工作簿中的 PLC 连接全部禁用，三轮确认实际加载了这份配置。该用例覆盖打包后的登录、只读状态、历史查询、回退和退出。

2026-11-25 用原 G6 包和重新构建的候选包完成演练。旧包、候选、回退三轮分别通过 20、19、19 项断言。统一入口的 19 项检查全部通过。原始 SQL 备份随本次临时环境清理，证据保留备份哈希和实际查询结果。 [PLC HMI 历史恢复与回退证据](verification/2026-11-25-plc-history-rollback.zip)

### PLC HMI 桌面回归与回退

在交互式 Windows 会话中传入 `--plc-desktop-baseline`，用上一版发布 ZIP、当前候选包和同一份测试配置完成桌面回归及维护锁交接。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismDesktop --plc-desktop-baseline C:/Temp/Previous/PlcHmi-ReleaseCandidate.zip --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

- 三轮分别验证登录、十个导航入口、模式与状态跟随、停止握手、轴和气缸参数、IO 分组与断线重连、报警和生产查询、系统设置、登出、托盘以及正常退出。关闭耗时采用 G0 上限。
- 候选版和回退版使用旧版创建的测试账号。每轮加载同一份生成的设备配置，唯一启用的设备是本机 OPC UA 回环。
- 旧版备份显示白班 41 件、夜班 17 件。备份后将测试库夜班数据更新为 23 件，候选版读回合计 64 件。回退版从另一个库恢复备份，再次读回 58 件和原报警记录。
- 旧版在系统设置中保存测试软件名称后退出。工装把工作簿、软件设置和 SQL 快照收进同一份校验归档。候选版读取备份中的名称后保存另一个名称，回退版则从同一份归档恢复原名称。各轮数据库连接分别指向对应的测试库。
- 桌面回归后，三个安装目录分别再启动一次。旧版通过界面上锁，退出后将锁记录恢复到候选版。候选版核对持锁人员和操作限制，由本人申请解锁并等待 PLC 确认。回退版恢复候选版留下的待确认记录，收到新的 PLC 停止反馈后清除它。每次切换都核对 PLC 请求写入次数。

汇总状态位于 `separateAcceptance.plcDesktopAndRollback`，`plc-desktop/report.json` 记录包、工作簿、SQL 备份、软件设置和两次交接的锁记录哈希，以及各阶段的断言与退出耗时。`desktop-backup-fixture.zip` 保存本轮用于恢复的测试归档。各实例的连接配置随测试环境生成。

2026-11-25 从实际保存的旧版软件设置、回环工作簿和 MySQL 测试库导出生成同一份备份。候选版读入原软件名称并另存新名称，回退版重新读回原名称和原产量 58 件。20 项工具测试、28 项完整离线检查通过。 系统设置备份和回退记录（本地归档 `2026-11-25-plc-settings-rollback.zip`）

2026-11-25 旧版、候选版、回退版分别通过 92、91、91 项断言。三轮确认关闭至窗口关闭耗时为 83、87、75 ms，窗口关闭至进程退出耗时为 178、206、174 ms。 三阶段桌面验证记录（本地归档 `2026-11-25-plc-desktop-rollback.zip`）

2026-11-25 补测维护锁交接后，旧版、候选版和回退版的交接用例分别通过 24、34、28 项断言，共 86 项。桌面与交接六轮共通过 363 项断言，完整离线复跑通过 26 项检查。 维护锁升级与回退验证记录（本地归档 `2026-11-25-plc-maintenance-rollback.zip`）

### PLC HMI OPC UA 回环

在交互式 Windows 会话中，为统一入口传入 `--plc-opcua` 与本机 MySQL Server 8 的 `bin` 目录。可同时加 `--plc-history-baseline` 复跑历史回退。两项使用独立临时配置和 MySQL 实例，Python 环境沿用 `openpyxl`。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismOpcUa --plc-opcua --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

工装校验并解包 PLC HMI 候选包，生成仅连接两台回环设备的测试工作簿。PLC01 的唯一可写绑定为 `b_Action[3]`，PLC02 仅提供只读 DI、DO。OPC UA 测试服务使用 `127.0.0.1` 上的两个临时端口，并检查实际监听地址。用例依次验证以下行为。

- 实际模式和八种状态反馈更新模式图。稳定反馈保留人工选中的页签，模式变化时保留当前顶层页面。
- 用界面停止按钮的按住和释放键事件发出 `True`、`False`。服务端分别记录一次写入，界面读回两次结果。重复键事件不多发，邻位保持原值。
- DI 和 DO 页面只读。断线时当前 IO 变为未知。恢复后连接代次递增，页面显示新会话的 DI 值，测试服务未收到旧停止握手的重放。
- 完整离线复跑中，PLC 回环和飞拍共享 IO 用例读取同一份 DI、DO 样本，逐行核对连接前后的值、质量和统计。验证范围见 [G1 共享 IO 验收](G0G1Verification.md)。

总报告的 `plcOpcUaLoopback` 记录本项结果。`plc-opcua/report.json` 记录候选包哈希、断言数量和详细日志。测试在专用回环服务结束后关闭临时 MySQL。

2026-11-25 使用重新构建的候选包完成回环复跑，44 项断言通过。统一入口合并历史回退、飞拍界面和流程后共 20 项检查通过。 [OPC UA 回环验证记录](verification/2026-11-25-plc-opcua-loopback.zip)

### PLC HMI 轴与气缸参数权限

在交互式 Windows 会话中使用 `--plc-parameters`，可与其他离线用例一起运行。工装为候选包生成轴和气缸的合成点位，运行独立的本机 OPC UA 服务和 MySQL 测试库。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismParameterPermissions --plc-parameters --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

用例通过候选包登录操作员、设备工程师、工艺工程师和管理员，也从未登录的工作区进入轴和气缸参数页。未登录用户和操作员在手动调试时仍可只读查看。有权限的角色须等到手动调试反馈确认后才能修改参数和操作按钮。回环服务记录实际界面产生的轴点动速度、使能、绝对位置运行、气缸到位延时及伸缩请求。退出调试时保持当前页面，复查只读状态和已打开编辑请求的提交限制。

汇总结果位于 `separateAcceptance.plcParameterPermissions`。详细证据保存在 `plc-parameters/report.json` 和 `plc-parameters/parameters.log`。

2026-11-25 完成参数权限复跑，209 项断言通过。统一入口连同历史回退、原 OPC UA 回环和飞拍仿真共 21 项检查通过。 [参数权限验证记录](verification/2026-11-25-plc-parameter-permissions.zip)

### PLC HMI 调试、初始化和维护人员锁

在交互式 Windows 会话中使用 `--plc-mode-operations`，可与其他离线用例一起运行。工装在候选包的隔离副本中启动 HMI，并使用本机 OPC UA 回环和独立 MySQL 测试库。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismModeOperations --plc-mode-operations --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

- 分别操作下方面板的“调试开关”和手动流程图的“参数调试／退出调试”，核对每次 `b_Action[6]` 写入与回读、PLC 状态确认和参数操作权限。PLC 确认进入手动调试后开放权限，清除请求时恢复只读。
- 分别操作手动模式流程图内的“初始化按钮”和下方的“按住初始化”，验证按下与松开、`b_Action[7]` 的置位与复位、PLC 初始化状态与完成信号，以及急停反馈下的按钮限制。
- 维护人员锁验证首人请求、多人持锁、本人验证解锁和管理员重新验证。核对人员记录落盘、轴和气缸保持只读，并通过写入入口验证持锁期间及等待 PLC 确认退出期间均拦截设备操作和参数写入。

汇总结果位于 `separateAcceptance.plcModeOperations`。详细证据保存在 `plc-mode-operations/report.json` 和 `plc-mode-operations/mode-operations.log`。

2026-11-25 首次运行模式操作专项通过 115 项断言，统一入口共通过 26 项检查。 模式操作验证记录（本地归档 `2026-11-25-plc-mode-operations.zip`）

2026-11-25 补测流程图调试入口和维护退出等待期间的写入限制后，正式工程候选包通过 139 项模式操作断言，完整离线复跑 28 项全部通过。记录包含候选包哈希、回环日志与完整复跑结论。 模式操作补测记录（本地归档 `2026-11-25-plc-mode-operation-coverage.zip`）

### PLC HMI 生命周期及按住期间断线

在交互式 Windows 会话中使用 `--plc-lifecycle`。工装在同一份隔离安装目录中按场景重启候选包，复用本机测试库和合成配置。

```powershell
python tools/verify_release_candidates.py --packages C:/Temp/PrismCandidates --output C:/Temp/PrismLifecycle --plc-lifecycle --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

- 首次启动前由回环端设置手动调试，验证直接打开轴参数页即按真实反馈获得操作权限。
- 验证取消关闭、最小化到托盘以及从原托盘菜单恢复最大化窗口。托盘期间保留后台采集连接。
- 让隐藏模式页的回调等待原按钮的松开复位，在等待结束前重新显示页面。验证等待结束后仍收到新的模式反馈，且未产生额外 PLC 写入。
- 分别在初始化和停止请求处于置位时断开连接。重连模拟 PLC 仍保留原请求位，核对旧松开事件没有跨连接写入。PLC 清零后，验证新一轮按下和松开都能正常执行。
- 验证按住初始化时关闭程序会清零，并从同一安装目录及测试库重新启动。每次关闭均记录确认至窗口消失和窗口消失至进程退出的耗时，分别按 G0 的 5 秒和 2 秒上限判定。
- 最后一个维护人员申请解锁后，让 PLC 保持维护状态并重启软件。验证待确认的人员锁随安装目录恢复，操作保持受限，解锁请求的重复写入为零。收到新的停止反馈后，界面和文件均清除这条待确认记录。
- 以占用锁文件路径的目录模拟读取故障。即使 PLC 返回有效的手动调试反馈，轴和气缸仍为只读，模式动作和参数提交均被拦截。

汇总结果位于 `separateAcceptance.plcLifecycle`。详细断言与关闭耗时位于 `plc-lifecycle/report.json` 和同目录的分阶段日志。

PLC 反馈失效时，模式图退出已经失效的按钮按住状态。实际清零仍使用原连接的写入上下文，未确认时界面提示核对 PLC。重新连接后以新会话读回的请求位决定按钮可用性。

2026-11-25 重新构建两个候选包并完成完整复跑，23 项检查全部通过。生命周期首次启动与重启分别通过 54 和 20 项断言。首次从确认关闭至窗口关闭耗时 93 ms，窗口关闭至进程退出耗时 149 ms。重启后两段耗时分别为 196 ms 和 246 ms。归档保留了旧包失败的断言及本轮结果。 生命周期验证记录（本地归档 `2026-11-25-plc-lifecycle.zip`）

2026-11-25 补测维护人员锁后，生命周期四轮分别通过 54、31、28、32 项断言，共 145 项。完整离线复跑通过 26 项检查。归档包含旧包读取故障的复现记录和修复后四轮结果。 维护锁重启验证记录（本地归档 `2026-11-25-plc-maintenance-restart.zip`）

2026-11-25 修复模式页可见性回调竞态后，生命周期四轮共通过 148 项断言。归档包含旧包复现、PLC 重连记录和新包复跑结果。 模式采集恢复验证记录（本地归档 `2026-11-25-h0-target-evidence.zip`）

## 配置和数据恢复

1. PLC HMI 退出后，备份旧安装的 `Config` 和该实例使用的数据库。新安装先还原对应的 `Config/PLC/CRT_HMI_PLC变量绑定配置.xlsx` 和 `Config/Database.local.json`。同一设备升级或回退时，如刚退出的实例有 `Config/maintenance-locks.json`，在目标安装首次启动前复制这份最新记录。做发布验收时把数据库备份还原为独立测试实例，连接串和设备绑定均指向测试环境。回退时退出候选版本，从保留的旧安装和对应备份启动。本次候选沿用 G4 数据结构。
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

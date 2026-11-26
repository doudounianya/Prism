# FlyScan 与 PLC HMI 界面统一验证

2026-11-26 在 Windows 独立工作树中，以 `3cf8cec` 为基线应用本次提交的代码和测试。FlyScan 使用内置运动卡仿真，PLC HMI 使用 OPC UA 回环设备和临时 MySQL 数据库。

## 结果

| 检查 | 结果 |
|---|---|
| 构建、打包与分层 | Debug 和 Release 均通过。两个候选包中的 `Company.Hmi.Presentation.dll` 一致，架构检查通过 |
| FlyScan 共享控制页 | 69 项断言通过。检查十项导航、顶部选中下划线、最小窗口宽度、诊断区默认折叠、模式与权限显示，以及连接正常、断开和重连的状态条反馈 |
| FlyScan 共享 IO 页 | 13 项断言通过。展开诊断区执行仿真断开和重连，核对实时值、历史值和未配置通道 |
| FlyScan 工艺页面与流程 | 示教与配方页签、表单和表格经截图复核。双组仿真、异常中断及恢复通过 |
| PLC HMI OPC UA 回环 | 45 项断言通过，覆盖模式反馈、IO 断开重连、停止握手和正常关闭 |
| PLC HMI 参数权限 | 209 项断言通过，覆盖轴与气缸参数的查看、手动调试写入及退出调试后恢复只读 |

统一离线入口共通过 20 项检查，0 项失败。两包公共界面程序集 SHA-256 为 `b44f337d2ba3b6bb8f602e5c9179f07d3ae07c3953a713c3a3b16d98ed3aeb3b`。

## 复跑

在 Windows 上准备 .NET SDK、MySQL Server 8 和本地 NuGet 缓存。以下命令生成候选包、验证报告和飞拍页面截图，两个输出目录应为新目录。

```powershell
$nugetSource = Join-Path $env:USERPROFILE '.nuget/packages'
python tools/package_release_candidates.py --restore-source $nugetSource --output C:/Temp/PrismUiCandidates
python tools/verify_release_candidates.py --packages C:/Temp/PrismUiCandidates --output C:/Temp/PrismUiVerification --restore-source $nugetSource --desktop --plc-opcua --plc-parameters --mysql-bin 'C:/Program Files/MySQL/MySQL Server 8.0/bin'
```

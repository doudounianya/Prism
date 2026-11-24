# 运动控制接入说明

公共 API 统一为 IMotionController。项目为 Company.Hardware.ControlCard，具体实现位于 None、INOVANCE 两个项目；三个项目归入 Hardware。结构与迁移示例见 [公共层导航](Company.Hardware.ControlCard/README.md)。

## 选择与初始化

通过 IMotionControllerFactory.Get(MotionDriverKind) 选择 Simulation、Inovance，每种实现按枚举名注册为命名单例。枚举数值保留 Simulation=0、Inovance=2，已移除的编号1不复用，也不回退到其他驱动。公共模块名为 MotionControllerModule；模块注册及服务解析不打开设备。

Initialize(MotionConfiguration) 直接接收配置并建立深快照。Enabled 默认 false，真实硬件还须 AllowNativeHardware=true，以及厂商协议确认选项。厂商 Options 同样在初始化时冻结，修改原配置须停止、关闭后重新初始化。

命令返回 true 表示请求接受，定位、回零和轨迹完成须读取有效反馈。SDK 调用共用串行边界，保留限位、急停、报警、任务占用、数据有效性及待确认动作校验。逻辑轴和插补组改为配置化标识；旧 X/Y/Z/U 和 Coordinate0 至 Coordinate3 仅为默认配置与调用便捷名称，不决定设备轴数。

AxisConfiguration.HardwareAxis 为零基物理索引，汇川直接使用。DigitalPointState.Bank/Bit 表示每组 16 位的 IO 地址，InputBankCount/OutputBankCount 是组数；不存在的端口通过 Available 单独标记，不显示成有效 OFF。

## 驱动能力

| 实现 | 已适配 | 未开放或待确认 |
| --- | --- | --- |
| Simulation（None 项目） | 单轴定位、相对位移、点动、回零、暂停恢复、停止、多点坐标、IO、报警/急停/限位注入 | 恒速流程模型，不模拟伺服惯量和真实保护回路 |
| Inovance | 单轴使能/定位/点动/暂停恢复/停止；物理映射和 IO 核对；原点已知状态、独立回零监督；XYZ 直线、XY/YZ/ZX 圆弧、前瞻缓冲、取消清理、关闭重试 | PSO、位置比较、龙门同步、振镜连续联动未开放；通用伺服故障复位待确认 |

MotionCapability 分开表达硬件支持、驱动实现、验证程度和当前就绪条件。SDK 存在函数不等于板卡型号、端子板、选件和驱动均支持。MotionControllerIdentity 无法可靠读取的型号、固件、FPGA 字段保留 Unknown；GetHwSysPara 的网络组合不当作型号。

## 汇川 IMC30G-E

InovanceNativeOptions 以下确认项默认 false，不代替 Enabled、AllowNativeHardware 或应用权限，也不自动下载配置、写当量或修改急停极性：

| 选项 | 含义 |
| --- | --- |
| HomingAxisUnitsPerSecondConfirmed | 已核对本机回零速度/加速度为轴单位/s、轴单位/s² |
| OriginInputBitConfirmed | 已核对当前固件原点输入为 C02 状态 bit13，否则 OriginKnown=false |
| InterpolationProtocolConfirmed | 已核对板卡、固件、轴绑定和普通插补协议，允许显式提交轨迹 |

现用 IMC_API_x64.dll 为 1.8.29.4，未用资料包 1.8.30.7 整包替换。原厂声明中的冲突参数及无界输出字符串入口继续隔离。

只在本会话实际提交回零/轨迹后开启对应监督，完成或明确清理后结束；构造、能力查询、页面加载不触发板卡轮询。任务超时使用单调计时，但软件等待不能中止卡住的非托管 API。

## 轨迹任务

StartTrajectory 接收不可变轨迹段，MoveLinearPath 是全直线便捷入口。终点为绝对位置，圆心为相对本段起点的平面偏移，不隐式接受整圆、多圈或螺旋。

只操作本会话拥有的坐标系，不覆盖外部坐标系。异步预填充、缓冲背压、多次 EndData 期间持续占用参与轴；暂停、Busy 暂时为 false 或队列暂空均不自动释放。恢复前核对暂停位置，圆弧校验包含途中极值。

自然完成须确认本任务进度、最终到位、停稳并解除持续使能。取消须停止并清队列，不表示到达原目标；读取或清理失败保留责任。WaitForTrajectory 跟随本次任务版本，取消等待只退出等待；停止任务需单独 CancelTrajectory。

## 原生库和应用边界

真实 SDK 仅支持 Windows x64，从程序目录 runtimes/win-x64/native 按绝对路径加载，不回退到工作目录；原生 DLL 不放进 Prism 扫描的 Modules。本轮只调整托管契约和命名，原生声明、SDK 二进制及选项默认值不变。

现有 HMI 的 PLC 轴页面仍使用原 PLC 来源。应用层负责用户权限、手动调试、维护互锁和操作日志；控制卡层负责状态有效性及动作校验。PLC 通讯、报警保存、产能同步和采集周期保持原路径。

完整解决方案 Debug/Release 构建会部署公共运动模块、仿真模块、汇川模块及原生依赖。SkipModuleDeployment=true 只编译不部署。单独发布 Shell 不代替 Modules 和 runtimes 的完整打包。外部旧插件需按公共层导航迁移后重新编译；离线检查不替代实际板卡时序、接线和精度验证。

十三轴示例与主从关系配置见 [可配置运动模型](MOTION_CONFIGURATION.md)。仿真已支持配置化主从跟随；汇川实际电子齿轮执行仍未开放，有启用同步关系时在 SDK 调用前拒绝初始化。

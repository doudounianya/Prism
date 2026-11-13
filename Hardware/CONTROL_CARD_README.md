# 运动控制卡接入说明

本次沿用原 Prism 模块/依赖注入和硬件关闭事件，四个 ControlCard 项目已加入 Program_Template.sln 的 Hardware 分组。PLC 通讯、PLC 轴页面、报警历史、产能同步及其采样周期保持现有路径。

## 选择与启用

- 三个实现分别命名注册为 None、AdTech、INOVANCE；通过 IControlCardFactory.Get(ControlCardKind) 选择，不再争用默认 IControlCard。
- 加载模块、注册服务、解析工厂和解析驱动都不初始化设备。Config.Enabled 默认 false；真实卡还须 Config.AllowNativeHardware=true，以及对应厂商协议选项确认。
- Config 和厂商 Options 在每次初始化时冻结。运行中修改原对象不会改变正在执行的协议；需停止、关闭后重新初始化。
- CurrentTime/状态集合由驱动控制，IControlCard 返回只读克隆。bool 表示请求接受，不表示已到位/回零完成；读取 Valid、Busy、Arrived、Homed 等反馈。不支持的能力返回 false，并通过 out message 说明，不会以空操作伪装成功。

仅离线仿真的示例：

```csharp
var factory = container.Resolve<IControlCardFactory>();
var card = factory.Get(ControlCardKind.None);
var config = new ControlCardConfig { Enabled = true }; // 仍未允许任何真实硬件
var provider = new ControlCardConfigSnapshotProvider(config);
if (card.Init(provider))
{
    // 应用操作入口仍需执行用户权限、模式、维护互锁与操作日志。
    // 仅在本离线 None 对象上明确请求使能/动作，读取状态后确认结果。
}
```

没有把当前 HMI 的 PLC 轴索引直接映射到控制卡。控制卡逻辑轴为 X/Y/Z/U，HardwareAxis 是零基物理索引：汇川直接使用；AdTech 转成厂商的一基轴号。具体页面/工艺的来源映射须明确后才能接实机。

## 已修复的范围

公共层修复相对门控、失败返回、初始化回滚、关闭重试、IO重复、无效参数及地址；SDK调用串行，操作前刷新状态，防止扫描延迟下的重复动作；等待有界可取消，超时保留待确认门控。保留旧18个方法，新增使能、复位、可报告结果的停止和等待接口。

None 已实现单轴定位、相对位移、点动、回零、暂停恢复、停止、共享轴的多点坐标、IO、报警/急停/限位注入及完整生命周期。它是恒速状态模型，用于验证流程，不模拟伺服惯量和真实安全回路。

## 真实驱动的边界

| 驱动 | 已适配 | 尚未确认、明确拒绝的能力 |
| --- | --- | --- |
| INOVANCE | 单轴使能/定位/点动/暂停恢复/停止、正确位置入参和轴号、批量状态、IO逐位/地址、句柄失败清理 | XYZ坐标ABI及完整协议；未确认参数单位的回零；未确认伺服故障复位。回零单位确认后使用 SDK InUnit 接口。Homed/Origin未知位不猜 |
| AdTech | 新接口签名、零基到一基轴号、绝对/相对模式、参数下发、状态/IO、回零启动及观测、停止、失败清理 | PTP暂停/恢复、完整XYZ轨迹和清空协议、未确认的保护输入语义。保守不虚构到位确认，短动作未观察到Busy时需明确停止解除待确认 |

AdTech 的 ProtectionInputValuesAreRawLevels 默认 null，AllowBusRestart、ApplyAxisConfiguration、ApplySoftLimits 默认 false。未确认保护语义/未授权总线重启时，在任何原生调用前拒绝初始化；默认不改单位/当量、不关闭卡上已有软限位。INOVANCE 默认不下载设备参数、不改急停极性、不写当量，先只读核对已有参数。

这两套 SDK 只允许 Windows x64。原生库从程序目录 runtimes/win-x64/native 按绝对路径加载，不回退到工作目录同名库。构建复制原SDK，不修改二进制；实际安装、固件和 ABI 仍需厂商资料及隔离实机确认。

回零超时的状态观测不等于独立安全看门狗；停止请求接受、句柄释放也不是机械已经停稳的证明。当前没有提前开启板卡后台采样，未改现有PLC周期。

## 构建与验证

Debug/Release解决方案构建会将本配置四个模块复制到 Shell/bin/<配置>/net8.0-windows/Modules，SDK复制到同一程序根下的 runtimes/win-x64/native。SkipModuleDeployment=true可用于只编译、不部署模块。

本次未改原工程的整体发布打包机制。单独 dotnet publish Shell 到全新目录不能代替完整动态模块打包；正式包应纳入完整解决方案构建的 Modules 与 runtimes。不能只拷Shell.exe或把原生DLL放进Prism扫描的Modules目录。

离线验证工程位于 tests/ControlCard.Verification，不在产品解决方案中。没有执行真实SDK、下载配置或驱动设备；实机启用另行确认。

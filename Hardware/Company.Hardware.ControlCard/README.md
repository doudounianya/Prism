# 运动控制公共层

以 `IMotionController` 表示一个运动控制会话，统一单轴、坐标系、轨迹、数字 IO 和能力查询。工厂选择厂商或仿真实现，基类执行公共校验，具体驱动实现相应的 `...Core` 扩展点。

项目路径、程序集和命名空间为 `Company.Hardware.ControlCard`，便于现有解决方案与模块部署定位；公共 API 按运动业务命名。项目内保留仿真和汇川两个驱动。

## 文件结构：11 个 C# 源文件

| 文件 | 职责 |
| --- | --- |
| `MotionControllerFactory.cs` | MotionDriverKind、工厂契约和实现；按 Simulation、Inovance 取得命名单例，关闭已解析实例。枚举保留数值0和2，不复用已移除编号1。 |
| `Contracts/IMotionController.cs` | 唯一公共控制契约，按会话、反馈、单轴、轨迹、IO、能力分组。 |
| `Configuration/MotionConfiguration.cs` | 轴、插补组及同步配置入口；初始化建立深快照。 |
| `Configuration/MotionConfigurationFile.cs` | 严格 JSON 读取/序列化及同步配置类型；不访问硬件。 |
| `Configuration/MotionConfigurationValidator.cs` | 逻辑标识、物理映射、组引用、单位及同步关系检查。 |
| `Models/MotionIdentifiers.cs` | 可配置 AxisId、CoordinateId 值对象及 JSON 转换。 |
| `Models/MotionModels.cs` | AxisId、CoordinateId、StopMode、JogDirection、状态、位置和轨迹段。 |
| `Models/MotionCapabilities.cs` | MotionFeature、MotionCapability、MotionControllerIdentity，描述支持、实现、验证和就绪情况。 |
| `Runtime/MotionControllerBase.cs` | 会话、资源、统一执行、配置校验、状态快照、IO 和能力查询。 |
| `Runtime/MotionControllerBase.Axis.cs` | 轴状态、运动准入、使能、回零、定位、点动、暂停恢复、停止及等待。 |
| `Runtime/MotionControllerBase.Coordinate.cs` | 坐标系、路径、任务版本、轴占用、路径校验、取消及等待。 |

三个 Runtime 文件仍为同一个 partial 类，共用锁、会话和资源。小型关联类型合并在同一文件，用中文区域折叠定位。

## 配置和状态

- `MotionConfiguration.Axes/Coordinates/Synchronizations` 保存轴、插补组及主从同步配置。Enabled 默认 false，真实硬件还须 AllowNativeHardware 和厂商协议确认。
- `AxisConfiguration.HardwareAxis` 为零基物理索引。PulsesPerUnit、PositioningVelocity、HomingSearchVelocity 等明确表示当量、定位和回零参数，驱动按已确认的轴单位使用。
- `CoordinateConfiguration` 定义 XAxis/YAxis/ZAxis、LinearVelocity、ArcVelocity、前瞻及到位容差。逻辑轴和组键可配置，不由固定枚举限制数量。组内轴数组按三个轨迹分量排序；控制器槽号独立保存。
- `AxisState.CommandPosition/ActualPosition` 区分规划与反馈位置；使能、报警、急停、回零、到位和有效性独立表达。
- `CoordinateState` 区分 Busy、Paused、Queued、Enabled、Cleared、Arrived；队列暂空或 Busy=false 不能单独表示任务完成。
- `DigitalPointState.Bank/Bit` 表示组及组内位，每组 16 位。InputBankCount/OutputBankCount 是组数，实际可用资源仍须由驱动核对。

公开状态集合每次返回独立副本。修改副本不会写硬件或改变内部运动判断。先确认 Valid，再解释状态；未知不当成有效 OFF。

## 调用示例：只使用离线仿真

```csharp
var factory = container.Resolve<IMotionControllerFactory>();
IMotionController controller = factory.Get(MotionDriverKind.Simulation);
var configuration = new MotionConfiguration
{
    Enabled = true,
    InputBankCount = 2,
    OutputBankCount = 2
};
if (controller.Initialize(configuration))
{
    controller.RefreshAxisState(AxisId.X);
    var x = controller.AxisStates.Single(s => s.Axis == AxisId.X);
    var capabilities = controller.GetCapabilities();
    // 先核对 x.Valid；实际操作还须执行用户权限、模式及维护互锁。
    controller.Close();
}
```

初始化直接接收配置，不再需要配置提供器、OnChanged 事件或能力接口转换。原配置随后改变不影响当前会话；换配置须停止、关闭后重新初始化。

运动库和厂商驱动不引用 Core、Prism、WPF 或 ReactiveUI。公共运动、仿真和汇川三个模块入口位于应用的 `Company.PlcHmi.Composition/Motion`。工厂接收 `Func<MotionDriverKind, IMotionController>`，由应用提供命名解析函数，脱离 Prism 的控制台也可以直接使用。SDK 调用与公共运动校验保持原实现。

MoveAbsolute、MoveRelative、HomeAxis 等返回请求是否接受；完成须另读有效反馈。TryStop 保留停止请求结果，确认停稳仍须读取状态。取消等待不自动停止运动。

MoveLinearPath 是全直线路径的便捷入口，与 StartTrajectory 共用校验及占用。轨迹终点为绝对位置，圆心为相对本段起点的平面偏移；不隐式产生整圆、多圈或螺旋。

## 两轴测试接入

业务代码依赖 [IMotionController](Contracts/IMotionController.cs)。使用 [两轴仿真配置](../../examples/Motion/TwoAxis.Simulation.json) 时，逻辑轴为 `AxisId.X`、`AxisId.Y`，示例轴号为 0、1，并包含一组仿真 DI 和 DO。轴号、毫米单位、软限位和速度均为该仿真示例的参数。

| 用途 | 公共接口 |
| --- | --- |
| 会话与能力 | `Initialize`、`Close`、`GetIdentity`、`GetCapabilities` |
| 单轴操作 | `SetAxisEnabled`、`ResetAxisFault`、`HomeAxis`、`MoveAbsolute`、`MoveRelative`、`JogAxis`、`TryStop` |
| 等待与确认 | `WaitForAxisIdle`、`RefreshAxisState`、`AxisStates` |
| 数字 IO | `RefreshInputs`、`InputStates`、`WriteOutput`、`RefreshOutputs`、`OutputStates` |

在应用或测试入口加载配置，再从工厂解析仿真控制器。

```csharp
var profile = MotionConfigurationFile.Load("examples/Motion/TwoAxis.Simulation.json");
profile.Enabled = true;
IMotionController controller = factory.Get(MotionDriverKind.Simulation);
if (!controller.Initialize(profile))
    throw new InvalidOperationException("两轴仿真初始化失败。");
```

操作方法的返回值表示请求是否被接受。业务以有效的 `Homed`、`Arrived`、`Busy` 和实际位置反馈决定下一步，结束会话时调用 `Close()`。两轴示例提供独立单轴动作，现有 `MoveLinearPath` 和 `StartTrajectory` 需要配置三个轴的插补组。

```powershell
dotnet run --project tests/Motion.Configuration.Smoke/Motion.Configuration.Smoke.csproj -- examples/Motion/FlyScan.13Axes.json
```

此入口一并复跑两轴、十三轴和原四轴默认配置。

## 驱动扩展和旧名称迁移

具体驱动继承 MotionControllerBase：OpenCore/ConfigureCore/CloseCore 管理资源，MoveAbsoluteCore/StartHomingCore 实现单轴协议，SubmitTrajectoryCore 等实现轨迹协议。所有扩展点先经过公共入口校验；SDK 声明、原生库和厂商选项仍在各厂商项目。

高级功能通过 MotionCapability 分开表达硬件支持、驱动实现、验证程度和当前可执行条件，不能按厂家名称推断。PSO 等未适配功能保持不可执行，身份和能力查询不连接硬件。

| 原名称 | 新名称 |
| --- | --- |
| IControlCard、能力查询接口、轨迹接口 | IMotionController |
| ControlCardFactory / ControlCardKind | MotionControllerFactory / MotionDriverKind |
| 驱动选择值 None / INOVANCE | Simulation / Inovance，厂商项目命名空间保持原名 |
| Init(provider) | Initialize(configuration) |
| ControlCardConfig / AxisSingleConfig / AxisThreeConfig | MotionConfiguration / AxisConfiguration / CoordinateConfiguration |
| AxisType / CrdType | AxisId / CoordinateId |
| AxisStatuss / CrdStatuss | AxisStates / CoordinateStates |
| PtpSingleMoveTo / PtpSingleMoveBy | MoveAbsolute / MoveRelative |
| GoHome / JogSingleMove | HomeAxis / JogAxis |
| CrdPause / CrdResume / CrdClr | PauseCoordinate / ResumeCoordinate / ClearCoordinate |
| OutputSetStatus | WriteOutput |
| 无结果 Stop(axis) | TryStop(axis, mode, out message) |

这是公共 API 调整。外部使用旧类型、旧配置属性名或旧模块名的代码需同步迁移和重新编译；完整映射见 reviews/motion-controller-redesign-20261117/rename-map.json。厂商支持及部署边界见 [运动控制接入说明](../CONTROL_CARD_README.md)。

十三轴映射、配置格式、兼容说明和同步能力边界见 [可配置运动模型](../MOTION_CONFIGURATION.md)。

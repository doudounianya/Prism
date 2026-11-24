# 可配置运动轴、插补组和同步关系

配置示例位于 `examples/Motion/FlyScan.13Axes.json`。它只描述设备，不注册后台任务、不控制机器人或视觉流程，也不自动初始化控制卡。示例的 `enabled` 和 `allowNativeHardware` 均为 false。

## 十三轴示例

| 逻辑轴 | 控制器轴号 | 总线 | 用途 |
| --- | --- | --- | --- |
| Front.X | 0 | Localbus | 正面拍照直线动子 X |
| Front.Y | 1 | Localbus | 正面拍照横向电机 Y |
| Front.R1 | 2 | Localbus | 正面拍照旋转电机 R1 |
| Front.R2 | 3 | Localbus | 正面拍照旋转电机 R2 |
| Unload.X | 4 | EtherCAT | 下料直线动子 |
| Back1.ArmZ | 5 | EtherCAT | 背面手臂 Z 轴 1 |
| Back1.WatchR | 6 | EtherCAT | 背面手表 R 轴 1 |
| Back1.FixtureX | 7 | EtherCAT | 背面治具 X 轴 1 |
| Back1.FixtureR | 8 | EtherCAT | 背面治具 R 轴 1 |
| Back2.ArmZ | 9 | EtherCAT | 背面手臂 Z 轴 2 |
| Back2.WatchR | 10 | EtherCAT | 背面手表 R 轴 2 |
| Back2.FixtureX | 11 | EtherCAT | 背面治具 X 轴 2 |
| Back2.FixtureR | 12 | EtherCAT | 背面治具 R 轴 2 |

轴号 0～12 已由用户确认。逻辑轴键、中文名称和硬件轴号分别保存，业务代码使用逻辑键；硬件轴号在同一卡内全局唯一，不按总线重新计数。逻辑键区分大小写，可包含字母、数字、点、下划线和短横线，以字母开头，最长64位。

直线轴使用 Millimetre，旋转轴使用 Degree。脉冲当量、速度、加速度、回零及行程参数沿用各轴配置字段；示例未提供现场标定值时采用软件默认值，不代表已经核对实际参数。汇川仍按原有选项核对现有当量，默认不下载或改写设备参数。

## 插补组

`Front.Scan` 使用控制器坐标系槽 0，`axes` 数组为 `[Front.X, Front.Y, Front.R1]`。数组顺序对应轨迹的第一个、第二个、第三个分量；`Position3D.Z` 在这个组中代表 R1 的角度，不要求存在名称叫 Z 的物理轴。

当前轨迹接口仍为三分量：可配置任意三个不同的逻辑轴，不隐式扩展为任意维轨迹。混合直线与旋转单位不在软件中擅自换算，合成速度和当量须与控制器配置相符。逻辑组名称与 `hardwareCoordinate` 分离；汇川槽号范围仍为0～3。多个组可以引用同一主轴，但运行时保持原有互斥占用。

原来的 `AxisId.X/Y/Z/U`、`CoordinateId.Coordinate0` 等名称保留为便捷标识；它们不再决定可用轴数量。`new MotionConfiguration()` 仍给出旧四轴默认配置，文件可以替换整个轴、组清单。原 XAxis/YAxis/ZAxis 属性是分量访问兼容入口，JSON 只保存 axes 数组。

## R1/R2 同步

`Front.RotationPair` 定义 Front.R1 为主轴、Front.R2 为从轴，比例1，跟随主轴命令位置。语义为“从轴位移 = 主轴位移 × 比例”，保留建立跟随时的零点偏差，不强制两根轴的绝对位置相等。负比例表示反向跟随。

配置校验拒绝不存在的轴、自跟随、重复从轴、主从环路或级联、零/非有限比例、不同单位及将从轴再次放入插补维度。从轴禁止独立定位、点动和回零；相关停止和故障按主从组处理，插补任务同时占用其从轴。

None 仿真器已执行这种位置/速度跟随，支持插补期间跟随、暂停及停止，用于离线流程验证。它不表示已验证真实电子齿轮时序。

汇川当前实现可读取并控制这13个已映射轴，也可按配置创建命名插补组；实际电子齿轮的建立、读回、退出及故障清理尚未接入。配置中有启用的同步关系时，真实驱动在任何 SDK 调用前明确拒绝初始化。不能把仿真通过解释为实机同步可用；后续驱动适配可通过能力扩展点接入，现有真实驱动不会默默忽略同步关系。

## 读取和使用

```csharp
var config = MotionConfigurationFile.Load(profilePath); // 只读文件并校验
var x = new AxisId("Front.X");
var scan = new CoordinateId("Front.Scan");

var simulator = new Company.Hardware.ControlCard.None.NoneControlCard();
config.Enabled = true; // 此示例只启用仿真
if (simulator.Initialize(config))
{
    simulator.SetAxisEnabled(null, true, out _);
    // 示教、拍摄握手与扫描顺序由应用流程编写。
    simulator.Close();
}
```

初始化对轴、组内轴数组和同步关系建立深快照。运行中修改配置对象不会改变既有会话；改配置需停止并重新初始化。读取或序列化配置不会触发初始化或原生库加载。

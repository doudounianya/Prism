# Prism HMI

基于 .NET 8 / WPF / Prism 的 HMI 工程，沿用原模块、依赖注入、数据库仓储和动态导航结构。

## 工程

- `Program_Template.sln`：19个工程的解决方案。
- `Company.Application.*`：登录、权限、监控、参数、报警、产能和操作日志。
- `Hardware/Company.Hardware.PLC*`：PLC运行与OPC UA驱动接口。
- `Hardware/Company.Hardware.ControlCard*`：控制卡公共层、None仿真、AdTech与INOVANCE实现。
- `Shell`：主程序。

## 构建

在Windows安装.NET 8 SDK及相应WPF开发工具：

```powershell
dotnet build Program_Template.sln -c Debug
dotnet build Program_Template.sln -c Release
```

构建按当前配置复制动态模块到Shell输出的Modules，原生SDK到`runtimes/win-x64/native`。运行真实驱动需要Windows x64及对应驱动安装。单独发布Shell不能代替完整动态模块打包，交付包必须包含完整Modules和runtimes。

## 本机配置

仓库不包含可用的数据库密码、账号缓存、现场Debug配置、历史日志或数据库备份。与本机工作副本相比，上传版数据库模块只将硬编码连接串替换为本机配置读取，保留原SqlSugar注册及仓储结构。

运行前，设置进程环境变量`CRT_HMI_DB_CONNECTION`，或在程序输出目录创建`Config/database.connection.local.txt`，其中放一行自己的数据库连接串。该文件已被Git忽略，不应提交。配置缺失时明确报错，不使用仓库中的默认密码。

可将`Shell/Properties/launchSettings.example.json`复制为同目录`launchSettings.json`后自行设置。示例默认关闭PLC写入、报警历史和产能同步，实际启用前须配置数据库、运行路径及PLC绑定。

`Shell/Config/PLC/CRT_HMI_PLC变量绑定配置.xlsx`为正式绑定模板，不含明文凭据；设备端点及凭据引用须按本机环境配置，凭据本体不入库。

## 控制卡

见 [控制卡接入说明](Hardware/CONTROL_CARD_README.md)。默认不初始化任何真实控制卡。需显式选卡、启用配置，并确认厂商协议。未经确认的XYZ、部分暂停/复位等功能明确拒绝，不以空操作报告成功。None用于离线流程验证，不模拟真实机械惯性或安全回路。

现有HMI的PLC轴路径保持独立，未将其自动改成直接控制运动卡；控制来源和轴映射需明确配置。

## 离线检查

```powershell
dotnet build tests/ControlCard.Verification/Verification.csproj -m:1 -p:SkipModuleDeployment=true
dotnet tests/ControlCard.Verification/bin/Debug/net8.0-windows/Verification.dll tests/controlcard-results.local.json
```

覆盖113项门控、生命周期、并发、None运动/坐标/IO及模块注入检查。测试不执行真实厂商SDK或设备动作。构建产物、日志、缓存和本机凭据均由`.gitignore`排除。

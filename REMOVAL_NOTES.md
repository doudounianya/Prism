# 相机与图像功能移除记录

日期：2026-10-21

## 已移除

- 相机接口、HIK 实现、模拟相机，探测器接口与模拟实现。
- 图像显示、预览、处理、滤波、绘图、图像流程以及 C++/CLI 算法包装模块。
- 对应共享接口、图像缓存枚举项、非托管图像内存类型、图像辅助方法及专用 UI 资源。
- 主界面图像区域与操作入口、相机菜单、相机及探测器配置页。
- 解决方案、项目引用、Prism 模块依赖和硬件生命周期中的对应引用。

## 保留

登录、菜单、软件配置、运动轴、控制卡、离散量 IO、数据库、日志及通用 UI。
主界面现在显示菜单和运动轴控制区域。通用字体图标等界面资源保留。
旧 JSON 中的相机及探测器字段读取时忽略；再次保存配置只输出软件、控制卡、IO 三个配置段。

为完成构建验证，同时将原有的双配置 xcopy 构建后脚本改为 MSBuild Copy，只复制当前构建配置的模块 DLL。

## 验证

- 完整解决方案 Debug Rebuild、Release Build 均通过，各为 0 错误、119 条警告；警告主要涉及现有代码的可空性等问题，本次未全面处理。
- 44 个项目 / XAML 文件 XML 解析通过；无失效的 ProjectReference。
- 源码、项目和解决方案未发现相机、探测器、图像处理模块引用；Shell 输出中无对应模块文件。
- 7 项隔离验证通过：旧配置兼容、保存字段清理、配置对象传递、仅控制卡和 IO 初始化、重复初始化拒绝、失败信息、控制卡关闭异常后仍关闭 IO。
- 验证使用模拟接口对象，没有实例化实际硬件驱动，也没有启动 HMI 或进行真机联调。
- 产品目标框架仍为 net8.0-windows；隔离验证程序使用本机已安装的 .NET 10 运行时。

## 备份与日志

均位于工程同级 CRT_HMI_Backups 文件夹：

- before-camera-image-removal-84a72385.zip：修改前完整源码备份（不含 bin、obj、.vs 等生成目录）。
- removed-camera-image-84a72385：从工程移出的模块和辅助文件归档。
- verification-camera-removal-84a72385：隔离验证源码、运行日志、Debug / Release 构建日志。

---

# 第一阶段业务清理记录

日期：2026-10-22。以下为当前状态，前文为 2026-10-21 的历史记录。

## 范围与结果

- 按用户确认的范围清理；未新增 PLC、Runtime 或设备业务工程。
- 从工作目录移出 10 个旧工程：Application.Axis、WpfHelper、ControlCard 基础及 INOVANCE / AdTech / None、Discrete 基础及 ModbusTcp / ModbusRtu / None。AdTech 原本未列入解决方案。
- 解决方案由 21 个工程减至 12 个工程；工作目录中的工程数由 22 个减至 12 个。
- 移出的工程完整保存在项目外 removed-projects 目录，包括原工程中的 SDK 和生成文件。
- 清理主界面 Axis 依赖、区域与布局，删除旧菜单和无功能入口；当前保留系统设置以及“设备通讯未启用”状态。
- 移除 HardwareLifetimeManager、InitResult 及旧控制卡 / IO 初始化。登录后先加载软件配置，再导航主界面，不执行硬件连接或读写。
- 清理 Share / Config / Initialize 的旧硬件引用、配置接口和配置页面。旧 JSON 的未知硬件字段可读取忽略，再保存时只输出软件配置。
- 清理登录页无效报警仓储依赖、未使用的报警对象及示例预填密码；数据库登录校验方式保持原样，现有登录缓存按哈希原样恢复。
- 清理课程标题、默认软件名称和 Shell 中旧 HslCommunication 预加载代码；保留 Prism 模块发现、IOC、异常日志及关闭事件机制。
- 移出 34 个未使用的办公示例图标、字体说明 PDF、Axis 专用样式与转换器。保留通用控件、语言、字体、枚举转换器及 WpfPropertyGrid。
- 本阶段共移出 99 个源码 / 资源文件，修改 25 个源码 / 工程文件；本记录文件另计。

## 保留与数据保护

- Company.Core、Company.Logger、Company.Database、Company.Database.SqlSugar 的 44 个源码 / 工程 / 配置文件与清理前 SHA-256 完全一致。
- 保留 Core 中现有通用依赖，不执行框架重构、依赖升级或数据库结构迁移。
- CRT_Data 清理前后逻辑导出文件 SHA-256 完全一致；member 与 alarminfo 表、管理员账号和密码均未修改。
- 已用只读 SQL 校验 admin 的既有密码及 Role=1 有效；未通过业务仓储执行建表、更新或删除。

## 验证

- 从空输出目录进行 Debug 和 Release 完整构建 / 重建，均为 0 错误、82 个警告；警告为现有可空性等问题，本次不扩展处理。
- 31 个项目 / XAML 文件 XML 解析通过，无失效的 ProjectReference。
- 源码、解决方案和 Debug / Release 输出无旧 Axis、控制卡、Discrete、厂商 SDK 或旧相机图像模块残留。
- 每个配置的 Modules 目录仅含 Config、Initialize、Login、Main、Menu、Database、Database.SqlSugar 七个现有模块 DLL。
- 项目外隔离启动验证 11 项通过：实际登录视图和视图模型解析、示例密码清理、登录至初始化再到主界面导航、管理员会话、模块发现、软件配置解析、菜单区域、设置 XAML、旧配置兼容、保存字段清理及主界面无 Axis。
- 隔离验证使用现有 Shell / Prism 启动链和模拟登录仓储，没有连接设备或写入实际数据库；验证工程只位于备份目录。

## 本次备份与恢复入口

工程同级 CRT_HMI_Backups/phase1-cleanup-20261022-090037：

- source-before-cleanup.zip：删除前 210 个源码 / 资源文件，压缩包内容已逐文件核对 SHA-256。
- source-manifest.json、protected-manifest.json：清理前文件、哈希及保护清单。
- runtime-state：85 个原运行配置文件副本。
- CRT_Data-before.sql、CRT_Data-after.sql：清理前后相同的数据库逻辑备份。
- removed-projects、removed-files：移出工程及辅助资源的完整归档。
- generated-before-cleanup：保留工程原来的 24 个 bin / obj 目录，含历史日志和运行状态。
- build-debug.log、build-release.log、verification.log：最终构建及隔离启动验证日志。
- verification：隔离验证源码及生成文件，不属于产品解决方案。

需要回退时先停止应用，从源码压缩包恢复源码 / SDK，再按需恢复对应运行配置和已归档生成文件。数据库未发生变化，无需导入备份；导入 SQL 会涉及覆盖既有数据，应另行确认。

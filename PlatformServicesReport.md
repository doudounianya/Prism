# 公共平台服务提取完成说明（2026-11-23）

新增 Company.Platform.Services 无界面项目，总计 24 个产品项目、15 个后台/契约/硬件库。账户认证、用户维护、共享会话、角色授权及登录偏好实现均可独立于 PLC HMI 使用。

## 应用边界

公共层通过 IAccountMutationPolicy 和 IUserAuthorizationPolicy 接收附加约束。PLC 调试状态、维护锁、页面范围及持锁人员账号保护保留在 HMI 共享项目；新增 PlcHmiPlatformModule 显式注册这些策略，再加载登录界面。IUserAccessService 的成员保持兼容，类型归属调整到 HMI 共享层，原适配器转发公共授权状态和版本。

Session 使用标准属性通知，不再依赖 ReactiveUI。登录偏好由保护接口实现，Windows DPAPI 位于桌面层，HMI 继续使用原用途、用户名熵和版本 1。CurrentUser 完整类型名、角色编号、数据库表/列、密码摘要格式、缓存键及记录字段均保留。

## 验证

离线回归 706/706：accounts-results.json=91; resource-results.json=114; motion-results.json=159; regression-results.json=119; runtime-results.json=48; platform-results.json=57; hmi-policy-results.json=118。另有架构图、原 Prism 目录发现、源码/配置审计和双配置编译。

- 公共服务在仅引用公共平台的 net8.0 控制台中运行，不加载 HMI、WPF、ReactiveUI、设备或 ORM 程序集。
- 验证管理员数据重验、账户创建/删除/重置、本人原密码验证、受保护缓存失效及应用策略的拒绝执行。
- HMI 验证完整角色/页面矩阵、未调试只读、有效反馈后授权、退出登录撤销、维护期间禁止动作但保留查看和解锁入口、持锁人员账户保护、权限版本和事件退订。
- 使用合成凭据验证旧 DPAPI 密文能被新服务恢复，新密文能按旧算法解密；未知版本/损坏密文关闭自动登录，不回退明文。
- 原生命周期/请求时序、页面资源、账户 SQLite、运动仿真回归继续通过。真实数据库、PLC 和运动卡未连接。
- 源码审计确认账户主体仅替换维护策略边界，偏好迁移与缓存写入顺序相同，DPAPI 算法相同，登录视图模型只替换依赖注入方式；其它控制、采集重连、日志与 SQL 源码保持一致。

Debug/Release 编译通过，仍有既有可空引用及厂商声明警告。本轮没有运行 HMI 或修改真实数据库，现场行为仍需在更新后重新启动并验收。

## 备份与接入

源码、现场配置备份：`F:\Myself\Project\Enovix\04_程序与配置\03_上位机软件\CRT_HMI_Backups\platform-services-20261123-085407`。更新前程序集另行备份，实际 Config 文件按哈希核对，不替换现场值。启动项目仍为 Company.PlcHmi.Shell，类型迁移要求整套程序集一起更新。

接口、职责、兼容用途和后续软件接入方式见 PlatformServices.md。新的飞拍应用尚未创建。

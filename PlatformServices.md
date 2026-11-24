# 公共账户与授权服务

## 当前职责

| 项目/类型 | 职责 |
| --- | --- |
| Company.Platform.Shared | CurrentUser、角色编号、账户/会话/授权/登录偏好契约与通用角色规则 |
| Company.Platform.Services.Accounts.UserAccountService | 认证、管理员用户维护、本人验证原密码改密及登录缓存失效 |
| Company.Platform.Services.Identity.Session | 共享当前身份，发布标准属性通知 |
| Company.Platform.Services.Authorization.UserAuthorizationService | 组合会话与应用策略，统一权限查询、变化通知和版本 |
| Company.Platform.Services.Login.RememberedLoginStore | 恢复登录偏好、保护格式迁移与缓存更新顺序 |
| Company.Desktop.Security.WindowsLoginSecretProtector | Windows CurrentUser DPAPI，实现平台凭据保护接口 |
| PLC HMI Shared/Login | 页面规则、PLC 调试/维护策略、维护持锁人员的账户保护及原 IUserAccessService 契约 |
| PLC HMI Composition/Platform | 选择并注册 HMI 策略、公共单例、Windows 保护器与兼容用途 |

新增一个公共服务项目，总计 24 个产品项目、15 个后台库。公共服务没有应用反向引用，能够在仅引用公共层的 net8.0 控制台中运行；角色权限不依赖 PLC、WPF 或数据库驱动。

## 未来软件如何组合

共同使用 ISession、IUserAccountService 与 IUserAuthorizationService。普通角色规则可以使用 RoleAuthorizationPolicy；有扫描流程、机器人互锁等条件时，由应用提供 IUserAuthorizationPolicy。默认未知权限拒绝，公共角色策略本身不代表设备已就绪。

```csharp
var session = new Company.Platform.Services.Identity.Session();
var policy = new Company.Platform.Services.Authorization.RoleAuthorizationPolicy();
var authorization = new Company.Platform.Services.Authorization.UserAuthorizationService(session, policy);
bool allowed = authorization.IsGranted(PlatformPermissions.ConfigureEquipment);
```

账户服务显式接收 IAccountMutationPolicy。没有设备附加约束时可选 NoAdditionalAccountMutationPolicy，管理员重验、自删/最后管理员保护、原密码校验仍由账户服务强制执行。需要人员锁的应用提供自己的策略，不把锁写进公共服务。

登录偏好接收 ILoginSecretProtector 与 LoginPreferenceOptions。Windows 软件可用桌面层的 WindowsLoginSecretProtector，新软件应选择自己的非空 ProtectionPurpose，登录偏好仍只是表单数据，恢复后必须重新认证才能建立会话。不同软件分别安装时，各自使用自己的 Config 目录。

使用 Prism 的宿主可以扫描公共服务程序集的 ExposedService 元数据，并显式注册应用策略、保护器与选项；也可以像纯控制台测试一样直接通过构造参数组合。公共服务没有注册数据库、导航或自动连接设备的副作用。

## PLC HMI 兼容边界

- Login 模块依赖新增的 PlcHmiPlatformModule，后者依赖原 DatabaseSqlSugarModule；公共服务先注册，UI 随后解析。模块目录现在发现 13 个应用模块，CoreModule 仍由 Shell 显式加入。
- 原 IUserAccessService 的成员不变，类型归属从平台共享层移到 Company.PlcHmi.Shared.Login。HMI 的 UserAccessService 是薄适配器，页面继续查询原成员，身份与权限版本只由一个公共授权服务维护。
- 原 UserAccessRules 的基本角色判断委托 RoleAccessRules；页面范围保持原样。手动调试与维护锁仅由 PlcHmiAuthorizationPolicy 解释。身份替换仍先撤销调试资格再通知版本变化，排队写入继续用原 Version 复核。
- 持锁人员仍不能被删除或由别人重置密码；管理员解除维护锁仍走原重新认证入口。参数页公开查看与设备动作权限分离的行为不变。
- UserRole 的 1/2/3/4、CurrentUser 的完整类型名和序列化字段、现有 Member 表/Password 列、密码摘要格式、用户名规则及缓存键保持兼容。这轮没有升级密码算法或执行数据库迁移。
- Session 不再继承 ReactiveObject，但保留身份替换的标准通知顺序与同引用不重复通知行为；CurrentUser 内部字段的直接修改仍不代表会话身份替换。
- HMI 明确使用原用途 `CRT_HMI.Company.PlcHmi.Login.RememberedPassword.v1|` 与默认用户名 admin。DPAPI 的 CurrentUser 范围、用途加用户名的熵、版本 1 和缓存字段名称保持一致。旧明文迁移先保护再返回，密文损坏或版本未知仍关闭自动登录，不回退明文。
- 旧 HMI 服务类型已经迁到公共命名空间，调用方必须重新编译；本轮已同步所有工程引用与模块输出，不能混用更新前的程序集。

## 验证与部署

```powershell
python tools/verify_architecture.py
dotnet run --project tests/Platform.Smoke/Platform.Smoke.csproj
dotnet run --project tests/HmiAuthorization.Smoke/HmiAuthorization.Smoke.csproj
dotnet run --project tests/Runtime.Smoke/Runtime.Smoke.csproj
dotnet build PrismFramework.sln -c Debug -m:1
dotnet build PrismFramework.sln -c Release -m:1
```

纯公共平台测试不引用应用与 ORM。HMI 权限测试只使用测试输出目录中的合成人员锁。Windows 密文兼容验证仅使用新生成的合成凭据，不读取或解密现场登录缓存。原账户 SQLite、页面资源、真实服务注册、生命周期与运动仿真回归继续执行。

更新实际输出前须结束运行中的 HMI，避免旧进程按需加载新程序集。源码、配置及原程序集有独立备份；部署核对现场 Config 不被替换。本轮不运行产品、不连接现场系统。结果见 PlatformServicesReport.md。

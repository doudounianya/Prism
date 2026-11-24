using Company.Core.Extension;
using Company.Desktop.Security;
using Company.Platform.Services.Accounts;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Shared.Login;
using Prism.Ioc;
using Prism.Modularity;

namespace Company.PlcHmi.Composition;

/// <summary>PLC HMI 的公共平台装配。未来软件复用服务项目，替换这里的应用策略和凭据用途即可。</summary>
[Module(ModuleName=Name,OnDemand=true)]
[ModuleDependency("DatabaseSqlSugarModule")]
public sealed class PlcHmiPlatformModule : IModule
{
    public const string Name="PlcHmiPlatformModule";
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IAccountMutationPolicy,PlcHmiAccountMutationPolicy>();
        registry.RegisterSingleton<IUserAuthorizationPolicy,PlcHmiAuthorizationPolicy>();
        registry.RegisterSingleton<ILoginSecretProtector,WindowsLoginSecretProtector>();
        // 这两个值用于兼容现有表单偏好和 DPAPI 熵，迁移中不能换成程序集新名称。
        registry.RegisterInstance(new LoginPreferenceOptions("CRT_HMI.Company.PlcHmi.Login.RememberedPassword.v1|","admin"));
        registry.RegisterAssembly(typeof(UserAccountService).Assembly);
    }
    public void OnInitialized(IContainerProvider provider) { } // 不认证、不恢复密码、不连接数据库。
}

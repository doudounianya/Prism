using Company.Database.Accounts;
using Company.Database.SqlSugar.Accounts;
using Company.Database.SqlSugar.Configuration;
using Company.Database.SqlSugar.Infrastructure;
using Prism.Ioc;
using Prism.Modularity;

namespace Company.Database.SqlSugar;

/// <summary>数据库适配器的 Prism 装配入口。只注册服务，不连接数据库、不执行建表。</summary>
[Module(ModuleName = Name)]
public sealed class DatabaseSqlSugarModule : IModule
{
    public const string Name = "DatabaseSqlSugarModule";

    public void RegisterTypes(IContainerRegistry registry)
    {
        if (!registry.IsRegistered<IDatabaseOptionsProvider>()) registry.RegisterSingleton<IDatabaseOptionsProvider>(() => new FileDatabaseOptionsProvider());
        if (!registry.IsRegistered<ISqlSugarSessionFactory>()) registry.RegisterSingleton<ISqlSugarSessionFactory, SqlSugarSessionFactory>();
        if (!registry.IsRegistered<AccountSchema>()) registry.RegisterSingleton<AccountSchema>();
        if (!registry.IsRegistered<IUserAccountRepository>()) registry.RegisterSingleton<IUserAccountRepository, UserAccountRepository>();
    }

    public void OnInitialized(IContainerProvider provider) { }
}

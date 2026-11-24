using Company.Database.SqlSugar;
using Company.PlcHmi.Persistence.Journal;
using Company.PlcHmi.Shared.Persistence;
using Prism.Ioc;
using Prism.Modularity;

namespace Company.PlcHmi.Persistence;

/// <summary>PLC 历史存储装配入口；通用数据库模块无需了解报警、产能或日志检查点。</summary>
[Module(ModuleName = Name)]
[ModuleDependency(DatabaseSqlSugarModule.Name)]
public sealed class PlcHmiPersistenceModule : IModule
{
    public const string Name = "PlcHmiPersistenceModule";

    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<HistoryTransaction>();
        registry.RegisterSingleton<IAlarmHistoryRepository, AlarmHistoryRepository>();
        registry.RegisterSingleton<IProductionHistoryRepository, ProductionHistoryRepository>();
        registry.RegisterSingleton<IProductionHistoryQueryRepository, ProductionHistoryQueryRepository>();
    }

    public void OnInitialized(IContainerProvider provider) { }
}

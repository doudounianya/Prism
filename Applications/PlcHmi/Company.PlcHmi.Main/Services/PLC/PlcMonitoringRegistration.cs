using Company.PlcHmi.Shared.PLC.Runtime;
using Company.Core.Ioc;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>模块初始化仅注册消费者；采集仍由原初始化流程显式启动。</summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class PlcMonitoringRegistration : IDisposable
{
    // 每个注册句柄独立保存；构造中后续注册失败时按已取得句柄回收，避免留下半套消费者。
    private readonly IDisposable _alarm;
    private readonly IDisposable _production;
    private readonly IDisposable _history;
    private readonly IDisposable _capacity;
    private readonly IDisposable _capacityHistory;

    /// <summary>
    /// 把报警实时显示与捕获注册到报警组，把计数显示、产能缓存及确认来源注册到产能组。
    /// </summary>
    public PlcMonitoringRegistration(IPlcAcquisitionService acquisition, AlarmMonitorService alarm, ProductionMonitorService production,
        Company.PlcHmi.Main.Services.History.PlcHistoryCaptureService history, ProductionCapacityService capacity,
        Company.PlcHmi.Main.Services.History.CapacityProductionHistorySource capacityHistory)
    {
        _alarm = acquisition.RegisterConsumer(alarm, ["BG.Alarm"]);
        try { _production = acquisition.RegisterConsumer(production, ["BG.Production"]); }
        catch { _alarm.Dispose(); throw; }
        try { _history = acquisition.RegisterConsumer(history, ["BG.Alarm"]); }
        catch { _alarm.Dispose(); _production.Dispose(); throw; }
        try { _capacity = acquisition.RegisterConsumer(capacity, ["BG.Production"]); }
        catch { _alarm.Dispose(); _production.Dispose(); _history.Dispose(); throw; }
        try { _capacityHistory = acquisition.RegisterConsumer(capacityHistory, ["BG.Production"]); }
        catch { _alarm.Dispose(); _production.Dispose(); _history.Dispose(); _capacity.Dispose(); throw; }
    }

    /// <summary>
    /// 解除本模块持有的全部消费者注册，使运行服务不再向这些实例分派采集帧。
    /// </summary>
    public void Dispose() { _alarm.Dispose(); _production.Dispose(); _history.Dispose(); _capacity.Dispose(); _capacityHistory.Dispose(); }
}

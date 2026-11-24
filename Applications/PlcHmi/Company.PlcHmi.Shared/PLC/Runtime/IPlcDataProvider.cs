using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
namespace Company.PlcHmi.Shared.PLC.Runtime;

/// <summary>跨模块只读缓存入口；获取目录、采集组或快照不会隐式建立连接或增加采集需求。</summary>
public interface IPlcDataProvider
{
    /// <summary>获取当前配置变量目录，调用者结合启用状态及权限选择可展示或可操作项。</summary>
    IReadOnlyList<PlcVariableBinding> GetCatalog();
    IReadOnlyList<PlcAcquisitionGroupDefinition> GetGroups();
    /// <summary>按明确键集合获取当前缓存快照；未启用或不具有读取权限的键会被拒绝。</summary>
    IReadOnlyList<PlcVariableSnapshot> GetSnapshots(IReadOnlyCollection<PlcVariableKey> keys);
    /// <summary>后台通知可合并；界面自行切换 UI 线程并重新取快照。</summary>
    event EventHandler? ValuesChanged;
}

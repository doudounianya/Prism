using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Application.PLC.Data;
using Company.Core.Ioc;
using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Models;

namespace Company.Application.PLC.Configuration;

/// <summary>把业务键、Excel 绑定、采集组及通讯点关联起来，供轮询、映射和调度共享同一配置语义。</summary>
internal sealed record PlcBoundPoint(PlcVariableKey Key, PlcVariableBinding Binding,
    PlcAcquisitionGroupDefinition Group, PlcPoint Point);
/// <summary>启动时生成的只读运行计划；Stamp 由配置版本与文件哈希组成，用于隔离配置切换前后的状态。</summary>
internal sealed record PlcRuntimePlan(PlcBindingConfiguration Configuration,
    IReadOnlyList<PlcDeviceDefinition> Devices, IReadOnlyDictionary<PlcVariableKey, PlcBoundPoint> Points)
{
    internal string Stamp => Configuration.Version + ":" + Configuration.FileSha256;
}

/// <summary>将通过加载校验的配置转换为可读运行点；再次约束当前实现支持的协议和采集周期组合。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcRuntimeConfigurationBuilder
{
    internal PlcRuntimePlan Build(PlcBindingConfiguration configuration)
    {
        var devices = configuration.Devices.Where(d => d.Enabled).ToArray();
        if (devices.Any(d => d.Protocol != PlcProtocol.OpcUa))
            throw new ArgumentException("本批仅支持 OpcUa，启用的其他协议设备不能启动。");
        var groups = configuration.AcquisitionGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var points = new Dictionary<PlcVariableKey, PlcBoundPoint>();
        // 计划仅纳入启用设备当前协议且具有读取权限的绑定；写入服务另从同一配置查找可写点。
        foreach (var binding in configuration.ActiveVariables.Where(v => v.Access.HasFlag(PlcAccess.Read)))
        {
            if (binding.Address is not OpcUaBindingAddress address || !groups.TryGetValue(binding.AcquisitionGroup, out var group))
                throw new ArgumentException("启用变量的协议地址或采集组无效。");
            if (group.Lifetime is not (PlcAcquisitionLifetime.Background or PlcAcquisitionLifetime.Page) ||
                group.Mode is not (PlcAcquisitionMode.PeriodicBatch or PlcAcquisitionMode.OnEnter) ||
                (group.Mode == PlcAcquisitionMode.PeriodicBatch && group.IntervalMs is not > 0) ||
                (group.Lifetime == PlcAcquisitionLifetime.Background && group.Mode != PlcAcquisitionMode.PeriodicBatch) ||
                (group.Mode == PlcAcquisitionMode.PeriodicBatch && group.StaleAfterMs is not > 0) ||
                (group.Lifetime == PlcAcquisitionLifetime.Background && group.CheckpointIntervalMs is not > 0))
                throw new ArgumentException("采集组生命周期或采集周期无效。");
            var key = new PlcVariableKey(binding.Protocol, binding.DeviceId, binding.LogicalId);
            var point = new PlcPoint(binding.LogicalId, binding.LogicalId,
                new OpcUaAddress(address.NamespaceUri, address.NodeIdentifier, address.ArrayIndex), PlcValueMapper.ClrType(binding.DataType));
            if (!points.TryAdd(key, new(key, binding, group, point))) throw new ArgumentException("启用变量标识重复。");
        }
        return new(configuration, Array.AsReadOnly(devices), new System.Collections.ObjectModel.ReadOnlyDictionary<PlcVariableKey, PlcBoundPoint>(points));
    }
}

using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>
/// 当前设备的产能绑定计划，固定配置版本、字段角色、时间解释策略及采集/过期周期。
/// </summary>
internal sealed record CapacityBindingPlan(string DeviceId, string Stamp, int StaleAfterMs,
    IReadOnlyDictionary<string, PlcVariableBinding> Fields, CapacityTimeEncoding TimeEncoding, int IntervalMs = 5000)
{
    internal PlcVariableKey Key(string field) => new(Fields[field].Protocol, DeviceId, field);
}

/// <summary>
/// 解析提升机六班次、每班十二小时的完整只读 OPC UA 绑定，拒绝类型或时间口径不一致的配置。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class CapacityBindingResolver
{
    // HMI 逻辑角色，不是 PLC 节点地址；实际地址全部来自 Excel。
    internal static string Shift(int slot) => $"Capacity[0].Shift[{slot}]";
    internal static string Hour(int slot, int hour) => $"{Shift(slot)}.Hour[{hour}]";
    /// <summary>
    /// 列出 HMI 约定的全部字段角色及原生类型，不生成或猜测实际 OPC UA 节点地址。
    /// </summary>
    internal static IReadOnlyDictionary<string, PlcDataType> Required()
    {
        var fields = new Dictionary<string, PlcDataType>(StringComparer.Ordinal);
        for (int s = 0; s < 6; s++)
        {
            fields.Add(Shift(s) + ".Start", PlcDataType.DateTime); fields.Add(Shift(s) + ".End", PlcDataType.DateTime);
            fields.Add(Shift(s) + ".Name", PlcDataType.String); fields.Add(Shift(s) + ".Quantity", PlcDataType.UInt32);
            for (int h = 0; h < 12; h++)
            {
                fields.Add(Hour(s, h) + ".Start", PlcDataType.DateTime); fields.Add(Hour(s, h) + ".End", PlcDataType.DateTime);
                fields.Add(Hour(s, h) + ".Quantity", PlcDataType.UInt32);
            }
        }
        return fields;
    }
    /// <summary>
    /// 仅在设备、采集组、完整唯一字段及时间编码均有效时生成计划，否则返回可显示的配置原因。
    /// </summary>
    internal CapacityBindingPlan? Resolve(PlcBindingConfiguration? config, string device, out string error)
    {
        error = "产能未配置：需要 st_CapacityDay[0] 六班次的完整只读绑定。";
        if (config is null || !config.Devices.Any(d => d.Enabled && d.Id == device && d.Protocol == PlcProtocol.OpcUa)) return null;
        var required = Required();
        // 每个必需角色必须恰好出现一次，防止缺字段或重复逻辑键被字典覆盖。
        var variables = config.ActiveVariables.Where(v => v.DeviceId == device && v.Category == "产能" && required.ContainsKey(v.LogicalId)).ToArray();
        if (variables.Length != required.Count || variables.Select(v => v.LogicalId).Distinct().Count() != required.Count) return null;
        var groups = config.AcquisitionGroups.Where(g => g.Id == "BG.Production").ToArray();
        var group = groups.Length == 1 ? groups[0] : null;
        if (group is null || group.Lifetime != PlcAcquisitionLifetime.Background || group.Mode != PlcAcquisitionMode.PeriodicBatch ||
            group.IntervalMs is not > 0 || group.StaleAfterMs is not > 0 || group.StaleAfterMs < group.IntervalMs) { error = "产能采集组配置无效。"; return null; }
        CapacityTimeEncoding? encoding = null;
        foreach (var v in variables)
        {
            var expected = required[v.LogicalId];
            // 时间角色允许 DateTime 或显式 UnixSecondsUtc 的 UInt32；数值转换、偏移与布尔反转都不得改变原生口径。
            bool type = v.DataType == expected || expected == PlcDataType.DateTime && v.DataType == PlcDataType.UInt32;
            if (!type || v.Protocol != PlcProtocol.OpcUa || !v.Access.HasFlag(PlcAccess.Read) || v.AcquisitionGroup != group.Id ||
                v.Scale.GetValueOrDefault(1) != 1 || v.Offset.GetValueOrDefault() != 0 || v.InvertBoolean ||
                v.Address is not OpcUaBindingAddress a || string.IsNullOrWhiteSpace(a.NamespaceUri) || string.IsNullOrWhiteSpace(a.NodeIdentifier))
            { error = "产能绑定的地址、原生类型、时间编码或采集组不完整：" + v.LogicalId; return null; }
            // 六班次全部时间字段共用一种解释策略，禁止把本地墙上时间和 UTC 时间混排比较。
            if (expected == PlcDataType.DateTime)
            {
                CapacityTimeEncoding policy;
                try { policy = CapacityTimeDecoder.Policy(v); }
                catch (ArgumentException ex) { error = ex.Message; return null; }
                if (encoding is { } previous && previous != policy)
                { error = "同一产能来源的时间编码必须一致：" + v.LogicalId; return null; }
                encoding = policy;
            }
        }
        return new(device, config.Version + ":" + config.FileSha256, group.StaleAfterMs.Value,
            variables.ToDictionary(v => v.LogicalId, StringComparer.Ordinal), encoding!.Value, group.IntervalMs.Value);
    }
}

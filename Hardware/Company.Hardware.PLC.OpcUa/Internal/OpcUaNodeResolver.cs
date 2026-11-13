using System.Globalization;
using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Models;
using Opc.Ua;

namespace Company.Hardware.PLC.OpcUa.Internal;

internal sealed class OpcUaNodeResolver
{
    private readonly Dictionary<string, ushort> _namespaceIndexes = new(StringComparer.Ordinal);

    internal void Reset(NamespaceTable? namespaces)
    {
        _namespaceIndexes.Clear();
        if (namespaces is null) return;
        for (var i = 0; i < namespaces.Count; i++)
            _namespaceIndexes.TryAdd(namespaces.GetString((uint)i), checked((ushort)i));
    }

    internal static PlcError? Validate(PlcPoint point)
    {
        if (point.Address is not OpcUaAddress address ||
            string.IsNullOrWhiteSpace(address.NamespaceUri) ||
            address.ArrayIndex is < 0 || string.IsNullOrWhiteSpace(address.NodeIdentifier) ||
            address.NodeIdentifier.Length < 3 || address.NodeIdentifier[1] != '=' ||
            "isgb".IndexOf(address.NodeIdentifier[0]) < 0 ||
            string.IsNullOrWhiteSpace(address.NodeIdentifier[2..]))
            return new(PlcErrorKind.InvalidAddress, "OPC UA 地址格式或数组下标无效。");
        try
        {
            _ = NodeId.Parse("ns=0;" + address.NodeIdentifier);
            return null;
        }
        catch (Exception ex) when (ex is ServiceResultException or FormatException or ArgumentException or OverflowException)
        {
            return new(PlcErrorKind.InvalidAddress, "节点标识应为不包含 ns= 的 i=、s=、g= 或 b= 格式。");
        }
    }

    internal (NodeId? Node, string? Range, PlcError? Error) Resolve(PlcPoint point)
    {
        var error = Validate(point);
        if (error is not null) return (null, null, error);
        var address = (OpcUaAddress)point.Address;
        if (!_namespaceIndexes.TryGetValue(address.NamespaceUri, out var index))
            return (null, null, new(PlcErrorKind.InvalidAddress, "当前会话不存在配置的命名空间 URI。"));
        var parsed = NodeId.Parse("ns=0;" + address.NodeIdentifier);
        return (new NodeId(parsed.Identifier, index), address.ArrayIndex?.ToString(CultureInfo.InvariantCulture), null);
    }
}

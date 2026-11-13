using Company.Hardware.PLC.OpcUa.Options;

namespace Company.Hardware.PLC.OpcUa;

public sealed class OpcUaPlcClientFactory
{
    /// <summary>每次创建独立客户端；仅校验参数，不连接设备。</summary>
    public OpcUaPlcClient Create(OpcUaConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new OpcUaPlcClient(options);
    }
}

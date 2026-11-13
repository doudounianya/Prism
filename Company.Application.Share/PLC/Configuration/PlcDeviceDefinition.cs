namespace Company.Application.Share.PLC.Configuration;

/// <summary>绑定协议标识；用于区分同一设备不同协议的变量键，运行支持范围由运行计划另行校验。</summary>
public enum PlcProtocol { Unknown, OpcUa, ModbusTcp }

/// <summary>Excel 设备配置快照，包含启用协议、端点、安全策略与重连参数；凭据只通过引用名关联本地身份。</summary>
public sealed record PlcDeviceDefinition
{
    public required PlcConfigurationSource Source { get; init; }
    public bool Enabled { get; init; }
    /// <summary>跨配置、运行缓存和业务查询使用的设备唯一标识。</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public PlcProtocol Protocol { get; init; }
    public string SheetSuffix { get; init; } = "";
    public string OpcUaEndpoint { get; init; } = "";
    public string SecurityMode { get; init; } = "";
    public string SecurityPolicy { get; init; } = "";
    /// <summary>本地连接身份的查找键；配置表只保存引用，不保存用户名密码或证书私钥。</summary>
    public string CredentialReference { get; init; } = "";
    /// <summary>设备建立连接的超时，单位毫秒；为空时由客户端工厂采用默认值。</summary>
    public int? ConnectionTimeoutMs { get; init; }
    /// <summary>连接失败后再次尝试的等待间隔，单位毫秒；为空时运行层采用默认值。</summary>
    public int? ReconnectIntervalMs { get; init; }
    public string ModbusHost { get; init; } = "";
    public int? ModbusPort { get; init; }
    public int? ModbusUnitId { get; init; }
    public string Notes { get; init; } = "";
}

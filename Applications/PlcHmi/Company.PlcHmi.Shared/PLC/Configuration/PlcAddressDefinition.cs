namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>Excel 地址定义。通讯层后续据此建立实际读写地址。</summary>
public abstract record PlcAddressDefinition;

/// <summary>OPC UA 绑定地址；NamespaceUri 按服务器命名空间解析，NodeIdentifier 和可选 ArrayIndex 定位节点或数组元素。</summary>
public sealed record OpcUaBindingAddress(
    string NamespaceUri, string NodeIdentifier, int? ArrayIndex) : PlcAddressDefinition;

/// <summary>保留 Modbus 区域、起始地址、位及寄存器长度和字节/字序配置；存在该模型不表示当前运行层已启用该协议。</summary>
public sealed record ModbusBindingAddress(
    string Area, int? StartAddress, int? Bit, int? RegisterCount,
    string ByteOrder, string WordOrder) : PlcAddressDefinition;

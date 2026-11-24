using System.Collections.ObjectModel;

namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>PLC 原始值类型；决定读取期望类型和写入转换边界，不能由显示文本自动推断。</summary>
public enum PlcDataType { Unknown, Bool, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String, DateTime }
/// <summary>变量访问位标志；Read 与 Write 可组合，运行读取和写入服务分别检查所需权限。</summary>
[Flags]
public enum PlcAccess { None = 0, Read = 1, Write = 2, ReadWrite = Read | Write }

/// <summary>业务逻辑变量与实际协议地址的绑定；地址、读写权限、工程量换算和采集组来自同一份配置。</summary>
public sealed record PlcVariableBinding
{
    public required PlcConfigurationSource Source { get; init; }
    public bool Enabled { get; init; }
    public PlcProtocol Protocol { get; init; }
    public string Category { get; init; } = "";
    /// <summary>变量逻辑标识，与 Protocol 和 DeviceId 共同形成运行唯一键。</summary>
    public string LogicalId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public required PlcAddressDefinition Address { get; init; }
    public PlcDataType DataType { get; init; }
    public PlcAccess Access { get; init; }
    public string AcquisitionGroup { get; init; } = "";
    /// <summary>工程量显示单位；数值换算依赖 Scale/Offset，单位文本本身不执行换算。</summary>
    public string Unit { get; init; } = "";
    /// <summary>数值比例，未配置按 1；读时工程量 = 原始值 × Scale + Offset，写时执行逆换算。</summary>
    public decimal? Scale { get; init; }
    /// <summary>工程量偏移，未配置按 0；与 Scale 配合用于数值转换。</summary>
    public decimal? Offset { get; init; }
    /// <summary>布尔值逻辑反相开关，读映射和写转换均遵循该绑定。</summary>
    public bool InvertBoolean { get; init; }
    /// <summary>允许写入的工程量下限；为空表示未在绑定中指定该侧范围。</summary>
    public decimal? Minimum { get; init; }
    /// <summary>允许写入的工程量上限；写入服务在逆换算之前检查。</summary>
    public decimal? Maximum { get; init; }
    /// <summary>业务写入语义，写入服务只接受明确支持的方式；不能仅凭 Write 权限发送未知方式。</summary>
    public string WriteMode { get; init; } = "";
    public string Notes { get; init; } = "";

    /// <summary>保留 IO、位置编号、报警说明等扩展字段；列号见 Source.Columns。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>仅表达配置，不创建连接、定时任务或仓储。</summary>
/// <remarks>文件哈希与配置版本共同组成运行配置标记，页面和写入流程据此识别配置是否已经切换。</remarks>
public sealed record PlcBindingConfiguration
{
    public const string SupportedVersion = "V0.8";
    public required string Version { get; init; }
    public required string FilePath { get; init; }
    /// <summary>本次实际加载文件的 SHA-256，用于区分同版本号下的不同绑定内容。</summary>
    public required string FileSha256 { get; init; }
    public required IReadOnlyList<PlcDeviceDefinition> Devices { get; init; }
    /// <summary>加载到的完整变量列表；包含非活动协议或未启用项，实际运行须使用 ActiveVariables 筛选。</summary>
    public required IReadOnlyList<PlcVariableBinding> Variables { get; init; }
    public required IReadOnlyList<PlcAcquisitionGroupDefinition> AcquisitionGroups { get; init; }

    /// <summary>只选择启用设备当前协议的启用绑定；两套协议同名映射不混用。</summary>
    public IEnumerable<PlcVariableBinding> ActiveVariables => Variables.Where(v => v.Enabled &&
        Devices.Any(d => d.Enabled && d.Id == v.DeviceId && d.Protocol == v.Protocol));
}

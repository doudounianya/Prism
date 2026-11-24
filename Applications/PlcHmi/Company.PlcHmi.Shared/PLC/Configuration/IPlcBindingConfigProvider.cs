namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>提供 Excel 绑定加载结果和上次成功配置；运行服务必须基于本次 Success 决定是否启动。</summary>
public interface IPlcBindingConfigProvider
{
    /// <summary>上次成功加载的快照。失败不覆盖；调用方仍须检查本次加载结果。</summary>
    PlcBindingConfiguration? Current { get; }
    /// <summary>最近一次加载的完整诊断结果，可与保留的 Current 成功快照区分。</summary>
    PlcConfigurationLoadResult? LastLoadResult { get; }
    /// <summary>读取并校验指定绑定文件；不建立 PLC 连接，取消与诊断通过本次结果或任务传播。</summary>
    Task<PlcConfigurationLoadResult> LoadAsync(string? filePath = null, CancellationToken cancellationToken = default);
}

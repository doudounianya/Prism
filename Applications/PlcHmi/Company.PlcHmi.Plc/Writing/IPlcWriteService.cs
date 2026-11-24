using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Hardware.PLC.Models;

namespace Company.PlcHmi.Plc.Writing;

/// <summary>业务写入入口；地址、类型及权限均取自当前 Excel，不创建独立连接。</summary>
/// <remarks>调用方先捕获会话和配置上下文；实际发送时仍会再次校验，连接变化后须重新读取并重新发起业务操作。</remarks>
public interface IPlcWriteService
{
    /// <summary>统一操作请求入口。已有便捷写入方法仍保留，全部转到同一执行管线。</summary>
    Task<PlcWriteReport> ExecuteAsync(PlcOperationRequest request, CancellationToken cancellationToken = default)
        => WriteSequenceAsync(request.Context, request.Steps, request.VerifyParameters, cancellationToken);
    /// <summary>当前进程是否启用实机写入；可写绑定及具体请求仍须通过服务校验。</summary>
    bool Enabled { get; }
    /// <summary>捕获已生效配置和已连接设备的代次，用于阻止旧页面上下文跨连接或配置边界发送。</summary>
    PlcWriteContext Capture(string deviceId);
    /// <summary>发送单步骤批量写入；verifyParameters 要求仅写参数暂存点，并逐项回读确认。</summary>
    Task<PlcWriteReport> WriteAsync(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> values,
        bool verifyParameters = false, CancellationToken cancellationToken = default);
    /// <summary>各步骤串行执行；前一步任一点失败或结果未知，后续步骤均不发送。</summary>
    Task<PlcWriteReport> WriteSequenceAsync(PlcWriteContext context,
        IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps, bool verifyParameters = false,
        CancellationToken cancellationToken = default);
}

/// <summary>一次固定目标、步骤顺序和参数校验方式的操作，不表达机械完成。请求编号用于诊断关联。</summary>
public sealed record PlcOperationRequest(PlcWriteContext Context,
    IReadOnlyList<IReadOnlyList<PlcVariableWrite>> Steps, bool VerifyParameters = false)
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>退出时由应用生命周期先处理操作，随后才能停止 PLC 连接。</summary>
public interface IPlcOperationLifecycle
{
    void Open();
    Task QuiesceAsync();
}

/// <summary>写入前提快照；连接/配置和授权版本在入队及真正发送前均复核。原清零请求仅豁免新动作授权。</summary>
public sealed record PlcWriteContext(string DeviceId, long ConnectionEpoch, string ConfigurationStamp, long AuthorizationVersion = 0);
/// <summary>待写变量及工程量文本；服务根据当前绑定完成反相、比例逆换算和类型检查，不接受独立地址。</summary>
public sealed record PlcVariableWrite(PlcVariableKey Key, string EngineeringValue);
/// <summary>逐项写入结果；Verified 为空表示未做回读，false 表示回读未确认一致，State 保留是否已发送及服务端结果。</summary>
public sealed record PlcVariableWriteResult(PlcVariableKey Key, PlcWriteState State,
    bool? Verified, string Message);
/// <summary>整次请求的逐项报告；仅非空、全部写入成功且没有回读失败时 Success 为 true。</summary>
public sealed record PlcWriteReport(IReadOnlyList<PlcVariableWriteResult> Results)
{
    public bool Success => Results.Count > 0 && Results.All(r => r.State == PlcWriteState.Succeeded && r.Verified != false);
}

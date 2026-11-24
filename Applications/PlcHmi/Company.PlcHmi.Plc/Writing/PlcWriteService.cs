using Company.PlcHmi.Plc.Connections;
using Company.PlcHmi.Plc.Data;
using Company.PlcHmi.Plc.Options;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Models;
using Company.PlcHmi.Shared.Login;
using Company.Core.Runtime;
using Company.Logger;
using System.Text.Json;

namespace Company.PlcHmi.Plc.Writing;

/// <summary>在所有步骤发送前完成校验和工程量转换的内部写入项；传输地址完全来自当前配置绑定。</summary>
internal sealed record PreparedWrite(PlcVariableWrite Request, PlcVariableBinding Binding, PlcWriteRequest Transport);

/// <summary>验证写入开关、配置上下文、变量权限和写入方式后，复用连接管理器执行串行步骤。</summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcWriteService), typeof(IPlcOperationLifecycle))]
public sealed class PlcWriteService(IPlcBindingConfigProvider configuration, IPlcRuntime runtime,
    PlcConnectionManager connections, PlcRuntimeOptions options, PlcOperationPolicy policy,
    IApplicationLifecycle lifecycle) : IPlcWriteService, IPlcOperationLifecycle
{
    private readonly object _operations = new();
    private int _active;
    private bool _sealed;
    private TaskCompletionSource _idle = CompletedIdle();
    private readonly Dictionary<(string Device, long Epoch, string Stamp, PlcVariableKey Key), PlcWriteContext> _held = new();
    public void Open()
    {
        if (lifecycle.State is not (ApplicationRunState.Created or ApplicationRunState.Starting))
            throw new InvalidOperationException("只能由后台启动流程开放操作入口。");
        lock (_operations)
        {
            if (_active != 0) throw new InvalidOperationException("仍有在途操作，不能重新开放入口。");
            _sealed = false;
        }
    }
    private static TaskCompletionSource CompletedIdle() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }
    public bool Enabled => options.EnableWrites;
    public PlcWriteContext Capture(string deviceId)
    {
        if (!Enabled) throw new InvalidOperationException("实机写入尚未启用，本批仅完成模拟验证。");
        var status = runtime.Status;
        var config = configuration.Current ?? throw new InvalidOperationException("PLC配置未加载。");
        var stamp = config.Version + ":" + config.FileSha256;
        var device = status.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (status.State != PlcRuntimeState.Running || device?.State != PlcDeviceState.Connected || status.ConfigurationVersion != stamp)
            throw new InvalidOperationException("PLC未连接或配置尚未生效。");
        return new(deviceId, device.ConnectionEpoch, stamp, policy.AuthorizationVersion);
    }
    public Task<PlcWriteReport> WriteAsync(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> values,
        bool verifyParameters = false, CancellationToken cancellationToken = default)
        => WriteSequenceAsync(context, new[] { values }, verifyParameters, cancellationToken);

    public Task<PlcWriteReport> WriteSequenceAsync(PlcWriteContext context,
        IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps, bool verifyParameters = false,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(new(context, steps, verifyParameters), cancellationToken);

    public async Task<PlcWriteReport> ExecuteAsync(PlcOperationRequest operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        // 在首次 await 之前复制请求，防止调用方在排队期间修改后续写入步骤。
        var context = operation.Context;
        IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps = operation.Steps.Select(s => (IReadOnlyList<PlcVariableWrite>)s.ToArray()).ToArray();
        var verifyParameters = operation.VerifyParameters;
        var requests = steps.SelectMany(s => s).ToArray();
        bool entered = false;
        try
        {
            lock (_operations)
            {
                if (_sealed) throw new InvalidOperationException("操作入口已关闭。");
                policy.Check(context, requests);
                if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entered = true;
            }
            if (requests.Length == 0 || steps.Any(s => s.Count == 0)) throw new ArgumentException("写入内容不能为空。");
            // 任何上下文字段变化均拒绝整次请求；不能把上一次连接或配置下用户确认的值继续发送到新会话。
            if (Capture(context.DeviceId) with { AuthorizationVersion = 0 } != context with { AuthorizationVersion = 0 }) throw new InvalidOperationException("连接或配置已变化，请重新读取后再操作。");
            var config = configuration.Current!;
            var bindings = config.ActiveVariables.ToDictionary(v => new PlcVariableKey(v.Protocol, v.DeviceId, v.LogicalId));
            var prepared = new List<IReadOnlyList<PreparedWrite>>();
            foreach (var step in steps)
            {
                if (step.Select(v => v.Key).Distinct().Count() != step.Count) throw new ArgumentException("同一步骤不能重复写入同一变量。");
                var batch = new List<PreparedWrite>();
                foreach (var request in step)
                {
                    MaintenanceInterlock.CheckWrite(request.Key.LogicalId, request.EngineeringValue);
                    // 只开放已确认的界面命令及停止位3；其余物理位、反馈及模式/状态枚举仍不可写。
                    if (request.Key.DeviceId != context.DeviceId || !bindings.TryGetValue(request.Key, out var binding)
                        || !binding.Access.HasFlag(PlcAccess.Write) || (binding.Category is not ("轴" or "气缸") && !IsModeCommand(binding))
                        || binding.Address is not OpcUaBindingAddress address || string.IsNullOrWhiteSpace(address.NodeIdentifier))
                        throw new ArgumentException($"{request.Key.LogicalId} 未启用或没有有效写入权限及地址。");
                    DebugParameterAccess.CheckWrite(binding.Category, request.Key.LogicalId, request.EngineeringValue, context.DeviceId, context.ConnectionEpoch, context.ConfigurationStamp);
                    // 参数校验模式只允许参数暂存，动作类写入不能混入回读确认流程；其余写入方式也必须在明确支持的集合内。
                    if (verifyParameters && binding.WriteMode != "参数暂存") throw new ArgumentException("参数下发不能混入动作命令。");
                    if (binding.WriteMode is not ("参数暂存" or "握手请求" or "保持请求" or "请求参数"))
                        throw new ArgumentException("写入方式未确认，不能发送。");
                    var point = new PlcPoint(binding.LogicalId, binding.LogicalId,
                        new OpcUaAddress(address.NamespaceUri, address.NodeIdentifier, address.ArrayIndex), PlcValueMapper.ClrType(binding.DataType));
                    batch.Add(new(request, binding, new(point, PlcWriteValueConverter.ConvertValue(binding, request.EngineeringValue))));
                }
                prepared.Add(batch);
            }
            // 验证完全部步骤再发送，不能写到中途才发现后续参数非法。
            var result = await connections.WriteSequenceAsync(context, prepared, verifyParameters, cancellationToken,
                () => policy.Check(context, requests)).ConfigureAwait(false);
            RememberHeld(context, requests, result);
            WriteDiagnostic(operation.RequestId, result);
            return result;
        }
        // 本层校验未通过时整批返回 NotSent；已进入通讯层的在途异常由连接管理器区分结果未知并禁止重发。
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or OverflowException or OperationCanceledException)
        {
            var rejected = new PlcWriteReport(requests.Select(v => new PlcVariableWriteResult(v.Key, PlcWriteState.NotSent, null,
                ex is OverflowException ? "数值超出PLC数据类型范围。" : ex is OperationCanceledException ? "操作已取消，未发送。" : ex.Message)).ToArray());
            WriteDiagnostic(operation.RequestId, rejected);
            return rejected;
        }
        finally { if (entered) lock (_operations) { if (--_active == 0) _idle.TrySetResult(); } }
    }

    private void RememberHeld(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> values, PlcWriteReport report)
    {
        lock (_operations)
        for (int i = 0; i < values.Count && i < report.Results.Count; i++)
        {
            var item = values[i];
            if (!PlcOperationPolicy.IsRelease(item with { EngineeringValue = "False" })) continue;
            var key = (context.DeviceId, context.ConnectionEpoch, context.ConfigurationStamp, item.Key);
            var state = report.Results[i].State;
            if (PlcOperationPolicy.IsRelease(item))
            { if (state == PlcWriteState.Succeeded) _held.Remove(key); }
            else if (item.EngineeringValue.Equals("True", StringComparison.OrdinalIgnoreCase) && state is PlcWriteState.Succeeded or PlcWriteState.OutcomeUnknown)
                _held[key] = context;
        }
    }

    /// <summary>先排空在途请求，再清零仍由本入口持有的原目标。维护请求不在清零名单内。</summary>
    public async Task QuiesceAsync()
    {
        Task idle;
        lock (_operations) idle = _idle.Task;
        await idle.ConfigureAwait(false);
        KeyValuePair<(string Device, long Epoch, string Stamp, PlcVariableKey Key), PlcWriteContext>[] held;
        lock (_operations) held = _held.ToArray();
        foreach (var item in held)
        {
            var result = await WriteAsync(item.Value, [new(item.Key.Key, "False")]).ConfigureAwait(false);
            if (!result.Success) Logs.LogWarning("退出时原按住/调试请求清零未确认；不跨连接重发：" + item.Key.Key.LogicalId);
        }
        // 与松开事件竞争时仍等待最后一个已接纳请求结束，封口之后不再接受任何写入。
        while (true)
        {
            lock (_operations) { if (_active == 0) { _sealed = true; return; } idle = _idle.Task; }
            await idle.ConfigureAwait(false);
        }
    }

    private static void WriteDiagnostic(string id, PlcWriteReport result)
    {
        try { Logs.LogInfo("HMI_REQUEST " + JsonSerializer.Serialize(new { RequestId = Guid.TryParseExact(id,"N",out _) ? id : "", Result = result.Results.Select(r => new { r.Key.DeviceId, r.Key.LogicalId, r.State, r.Verified }), Meaning = "仅为通讯写入结果，不表示机械完成" })); }
        catch { /* 诊断失败不能妨碍按住请求清零或改变实际报告。 */ }
    }

    /// <summary>设备状态类别不开放通用写入，只接受本机明确角色及数组下标；停止位3由用户确认采用按下/松开。</summary>
    internal static bool IsModeCommand(PlcVariableBinding binding)
    {
        if (binding.Category != "设备状态" || binding.ComponentId != "UnitMode[0]" || binding.DataType != PlcDataType.Bool ||
            binding.Address is not OpcUaBindingAddress address || !address.NodeIdentifier.EndsWith(".System.st_UnitMode[0].b_Action", StringComparison.Ordinal)) return false;
        return (binding.LogicalId, address.ArrayIndex, binding.WriteMode) is
            ("UnitMode[0].Action[3]", 3, "握手请求") or
            ("UnitMode[0].Request.Debug", 6, "保持请求") or
            ("UnitMode[0].Request.Initialize", 7, "握手请求") or
            ("UnitMode[0].Request.Maintenance", 8, "保持请求");
    }
}

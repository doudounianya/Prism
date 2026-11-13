using Company.Application.PLC.Connections;
using Company.Application.PLC.Data;
using Company.Application.PLC.Options;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Models;
using Company.Application.Share.Login;

namespace Company.Application.PLC.Writing;

/// <summary>在所有步骤发送前完成校验和工程量转换的内部写入项；传输地址完全来自当前配置绑定。</summary>
internal sealed record PreparedWrite(PlcVariableWrite Request, PlcVariableBinding Binding, PlcWriteRequest Transport);

/// <summary>验证写入开关、配置上下文、变量权限和写入方式后，复用连接管理器执行串行步骤。</summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcWriteService))]
public sealed class PlcWriteService(IPlcBindingConfigProvider configuration, IPlcRuntime runtime,
    PlcConnectionManager connections, PlcRuntimeOptions options) : IPlcWriteService
{
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
        return new(deviceId, device.ConnectionEpoch, stamp);
    }
    public Task<PlcWriteReport> WriteAsync(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> values,
        bool verifyParameters = false, CancellationToken cancellationToken = default)
        => WriteSequenceAsync(context, new[] { values }, verifyParameters, cancellationToken);

    public async Task<PlcWriteReport> WriteSequenceAsync(PlcWriteContext context,
        IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps, bool verifyParameters = false,
        CancellationToken cancellationToken = default)
    {
        var requests = steps.SelectMany(s => s).ToArray();
        try
        {
            if (requests.Length == 0 || steps.Any(s => s.Count == 0)) throw new ArgumentException("写入内容不能为空。");
            // 任何上下文字段变化均拒绝整次请求；不能把上一次连接或配置下用户确认的值继续发送到新会话。
            if (Capture(context.DeviceId) != context) throw new InvalidOperationException("连接或配置已变化，请重新读取后再操作。");
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
                    // 模式控制仅扩展三个已确认的界面命令。物理位 0..5、反馈及模式/状态枚举仍不可写。
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
            return await connections.WriteSequenceAsync(context, prepared, verifyParameters, cancellationToken).ConfigureAwait(false);
        }
        // 本层校验未通过时整批返回 NotSent；已进入通讯层的在途异常由连接管理器区分结果未知并禁止重发。
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or OperationCanceledException)
        {
            return new(requests.Select(v => new PlcVariableWriteResult(v.Key, PlcWriteState.NotSent, null,
                ex is OverflowException ? "数值超出PLC数据类型范围。" : ex is OperationCanceledException ? "操作已取消，未发送。" : ex.Message)).ToArray());
        }
    }

    /// <summary>设备状态类别不开放通用写入，只接受本机 UnitMode[0] 三个逻辑角色及对应数组下标。</summary>
    internal static bool IsModeCommand(PlcVariableBinding binding)
    {
        if (binding.Category != "设备状态" || binding.ComponentId != "UnitMode[0]" || binding.DataType != PlcDataType.Bool ||
            binding.Address is not OpcUaBindingAddress address || !address.NodeIdentifier.EndsWith(".System.st_UnitMode[0].b_Action", StringComparison.Ordinal)) return false;
        return (binding.LogicalId, address.ArrayIndex, binding.WriteMode) is
            ("UnitMode[0].Request.Debug", 6, "保持请求") or
            ("UnitMode[0].Request.Initialize", 7, "握手请求") or
            ("UnitMode[0].Request.Maintenance", 8, "保持请求");
    }
}

using Company.Application.PLC.Configuration;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Hardware.PLC.Interfaces;
using Company.Hardware.PLC.Models;
using Company.Logger;
using Company.Application.PLC.Writing;
using Company.Application.PLC.Options;
using Company.Application.Share.Login;
using Company.Hardware.PLC.OpcUa.Models;

namespace Company.Application.PLC.Connections;

/// <summary>集中管理每台设备的会话、健康检测、自动重连与串行读写；业务层只持有设备标识和会话代次。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcConnectionManager(PlcClientFactory factory, IPlcConnectionProfileProvider profiles, PlcRuntimeOptions options)
{
    // OPC UA 标准只读节点 ns=0;i=2258，不依赖业务标签或 PLC 程序增加心跳位。
    private static readonly PlcPoint HealthPoint = new("__CommunicationCheck", "__CommunicationCheck",
        new OpcUaAddress("http://opcfoundation.org/UA/", "i=2258"), typeof(DateTime));
    /// <summary>单设备运行槽位：Gate 串行化客户端操作，StateSync 原子保护状态和结果提交，Wake 可唤醒重连等待。</summary>
    private sealed class Device(PlcDeviceDefinition definition)
    {
        internal readonly PlcDeviceDefinition Definition = definition;
        internal readonly object StateSync = new();
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly SemaphoreSlim Wake = new(0, 1);
        internal IPlcClient? Client;
        internal EventHandler<PlcConnectionStateChangedEventArgs>? StateHandler;
        internal PlcDeviceRuntimeStatus Status = new(definition.Id, PlcDeviceState.Disconnected, 0, DateTimeOffset.UtcNow, null);
        internal volatile bool Reconnect;
        internal string? LastReadError;
        internal Task Worker = Task.CompletedTask;
    }
    // 全局锁只保护设备字典和停止令牌的获取；耗时通讯操作使用各设备 Gate，避免一台设备阻塞所有设备。
    private readonly object _sync = new();
    private Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private string? _planStamp;
    internal event Action<PlcDeviceRuntimeStatus>? StatusChanged;
    internal IReadOnlyList<PlcDeviceRuntimeStatus> GetStatuses()
    { lock (_sync) return Array.AsReadOnly(_devices.Values.Select(d => Volatile.Read(ref d.Status)).ToArray()); }
    internal void Start(PlcRuntimePlan plan)
    {
        _stop = new();
        _planStamp = plan.Stamp;
        lock (_sync) _devices = plan.Devices.ToDictionary(d => d.Id, d => new Device(d), StringComparer.Ordinal);
        foreach (var device in _devices.Values)
            device.Worker = Task.Run(() => ConnectLoop(device, _stop.Token));
    }
    /// <summary>发布不可变设备状态；仅在健康读取确认新连接后推进 ConnectionEpoch，Connecting 状态计入连接尝试次数。</summary>
    private void SetState(Device device, PlcDeviceState state, string? error = null, bool newEpoch = false)
    {
        lock (device.StateSync)
        {
            var old = Volatile.Read(ref device.Status);
            if (state == old.State && error == old.Error && !newEpoch) return;
            var next = old with { State = state, ConnectionEpoch = old.ConnectionEpoch + (newEpoch ? 1 : 0),
                ChangedAt = DateTimeOffset.UtcNow, Error = error,
                ConnectionAttempts = old.ConnectionAttempts + (state == PlcDeviceState.Connecting ? 1 : 0) };
            Volatile.Write(ref device.Status, next);
            // 在状态锁内通知边界观察者，保证断线与样本提交顺序一致；订阅者应快速完成且不得反向等待设备 Gate。
            StatusChanged?.Invoke(next);
        }
    }
    /// <summary>每设备唯一连接工作循环；连接失败按配置间隔重试，已连接时按独立健康检测间隔读取标准节点。</summary>
    private async Task ConnectLoop(Device device, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 连接、释放、健康读取与业务读写共用 Gate；替换会话时不会与正在执行的请求并发操作旧客户端。
                await device.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (device.Reconnect || device.Status.State != PlcDeviceState.Connected || device.Client?.ConnectionState != PlcConnectionState.Connected)
                    {
                        device.Reconnect = false;
                        SetState(device, PlcDeviceState.Connecting);
                        await ReleaseClientAsync(device).ConfigureAwait(false);
                        var profile = await profiles.ResolveAsync(device.Definition, token).ConfigureAwait(false);
                        var client = factory.Create(device.Definition, profile);
                        device.Client = client;
                        device.StateHandler = (_, e) =>
                        {
                            lock (device.StateSync)
                            {
                                // SDK 通知可能迟到；旧会话和主动释放产生的通知不能污染新会话。
                                if (!token.IsCancellationRequested && ReferenceEquals(device.Client, client) &&
                                    client.ConnectionState == e.Current &&
                                    e.Current is PlcConnectionState.Faulted or PlcConnectionState.Disconnected)
                                    SetState(device, PlcDeviceState.Faulted, ErrorText(e.Error, "PLC 通讯已断开。"));
                            }
                        };
                        client.ConnectionStateChanged += device.StateHandler;
                        await client.ConnectAsync(token).ConfigureAwait(false);
                        // 连接成功后必须实际读取成功，才能发布 Connected 和新会话边界。
                        if (await CheckCommunicationAsync(device, token).ConfigureAwait(false))
                            lock (device.StateSync)
                            {
                                if (client.ConnectionState == PlcConnectionState.Connected && !device.Reconnect)
                                    SetState(device, PlcDeviceState.Connected, newEpoch: true);
                            }
                    }
                    else await CheckCommunicationAsync(device, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // 不记录异常对象，避免 SDK 的端点/身份错误泄露凭据。
                    SetState(device, PlcDeviceState.Faulted, "PLC 连接失败，请检查配置、凭据及服务器状态。");
                }
                finally { device.Gate.Release(); }
                // Wake 将人工重连请求合并为一次唤醒；超时后继续正常检测或重试，运行停止令牌中断整个循环。
                var delay = device.Status.State == PlcDeviceState.Connected ? options.HealthCheckIntervalMs :
                    device.Definition.ReconnectIntervalMs ?? 3000;
                await device.Wake.WaitAsync(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    /// <summary>先移除当前客户端引用及状态订阅，再释放旧会话，避免主动释放产生的迟到通知污染设备状态。</summary>
    private static async Task ReleaseClientAsync(Device device)
    {
        var client = device.Client;
        device.Client = null;
        if (client is null) return;
        if (device.StateHandler is { } handler) client.ConnectionStateChanged -= handler;
        device.StateHandler = null;
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { Logs.LogWarning($"PLC {device.Definition.Id}：旧会话释放失败，将重新建立连接。"); }
    }
    /// <summary>实际读取标准服务端时间节点；只有返回一项成功且非默认的 DateTime 才视为通讯有效。</summary>
    private async Task<bool> CheckCommunicationAsync(Device device, CancellationToken token)
    {
        var client = device.Client!;
        var batch = await client.ReadBatchAsync([HealthPoint], token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (client.ConnectionState != PlcConnectionState.Connected || batch.Error is not null ||
            batch.Results.Count != 1 || batch.Results[0] is not { Success: true, Value: DateTime time } || time == default)
        {
            SetState(device, PlcDeviceState.Faulted, "PLC 通讯检测失败，将自动重连。" +
                ErrorText(batch.Error ?? batch.Results.FirstOrDefault()?.Error, "未收到有效的服务端响应。"));
            return false;
        }
        lock (device.StateSync)
            Volatile.Write(ref device.Status, device.Status with { LastCommunicationAt = DateTimeOffset.UtcNow });
        return true;
    }
    // 只把连接、超时、会话或安全通道类错误当作断线；单个业务节点的地址或权限问题不必然要求重建连接。
    private static bool IsCommunicationError(PlcError? error) => error?.Kind is
        PlcErrorKind.NotConnected or PlcErrorKind.Timeout or PlcErrorKind.Communication or PlcErrorKind.Disposed ||
        error?.NativeStatusCode is Opc.Ua.StatusCodes.BadNotConnected or Opc.Ua.StatusCodes.BadServerNotConnected or
            Opc.Ua.StatusCodes.BadConnectionClosed or Opc.Ua.StatusCodes.BadCommunicationError or
            Opc.Ua.StatusCodes.BadNoCommunication or Opc.Ua.StatusCodes.BadSessionClosed or
            Opc.Ua.StatusCodes.BadSessionIdInvalid or Opc.Ua.StatusCodes.BadSecureChannelClosed or
            Opc.Ua.StatusCodes.BadSecureChannelIdInvalid or Opc.Ua.StatusCodes.BadTimeout or Opc.Ua.StatusCodes.BadRequestTimeout;
    private bool CheckReadConnection(Device device, PlcBatchResult<PlcReadResult> batch)
    {
        var error = IsCommunicationError(batch.Error) ? batch.Error :
            batch.Results.Select(r => r.Error).FirstOrDefault(IsCommunicationError);
        if (device.Client?.ConnectionState == PlcConnectionState.Connected && error is null) return true;
        SetState(device, PlcDeviceState.Faulted, ErrorText(error ?? batch.Error, "PLC 读取后连接失效，将自动重连。"));
        return false;
    }
    internal bool IsConnected(string deviceId)
    {
        lock (_sync) return _devices.TryGetValue(deviceId, out var device) &&
            Volatile.Read(ref device.Status).State == PlcDeviceState.Connected;
    }
    /// <summary>在设备串行门内批量读取，并仅在会话仍有效时回调提交；返回 false 表示本轮未被接纳。</summary>
    internal async Task<bool> ReadAsync(string deviceId, IReadOnlyList<PlcPoint> points,
        Action<PlcBatchResult<PlcReadResult>, long> commit, CancellationToken token)
    {
        Device device; lock (_sync) device = _devices[deviceId];
        await device.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (device.Client is null || device.Status.State != PlcDeviceState.Connected) return false;
            // 在发出请求前捕获会话代次；返回后在 StateSync 下复核状态与代次，阻止异步断线通知之后提交旧数据。
            var epoch = device.Status.ConnectionEpoch;
            PlcBatchResult<PlcReadResult> batch;
            try { batch = await device.Client.ReadBatchAsync(points, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            { SetState(device, PlcDeviceState.Faulted, "PLC 批量读取发生通讯错误。"); return false; }
            if (!CheckReadConnection(device, batch)) return false;
            var readError = batch.Error is null ? null : ErrorText(batch.Error, "PLC 读取请求被拒绝。");
            if (readError != device.LastReadError && readError is not null)
                Logs.LogWarning($"PLC {device.Definition.Id}：{readError}");
            device.LastReadError = readError;
            lock (device.StateSync)
            {
                if (device.Status.State != PlcDeviceState.Connected || device.Status.ConnectionEpoch != epoch) return false;
                if (batch.Error is null && batch.Results.Any(r => r.Success))
                    Volatile.Write(ref device.Status, device.Status with { LastCommunicationAt = DateTimeOffset.UtcNow });
                commit(batch, epoch); // 与异步断线通知共用状态锁，旧结果不能跨越断线边界。
                return true;
            }
        }
        finally { device.Gate.Release(); }
    }
    private static string ErrorText(PlcError? error, string fallback) => error is null ? fallback :
        error.Message + (error.NativeStatusCode is { } code ? $" (0x{code:X8})" : "");
    /// <summary>持有设备门串行执行已校验的写入步骤；任一步失败、取消、回读不一致或上下文失效即停止后续步骤。</summary>
    internal async Task<PlcWriteReport> WriteSequenceAsync(PlcWriteContext context,
        IReadOnlyList<IReadOnlyList<PreparedWrite>> steps, bool verify, CancellationToken token)
    {
        var results = steps.SelectMany(s => s).Select(p => new PlcVariableWriteResult(p.Request.Key,
            PlcWriteState.NotSent, null, "前一步未成功，未发送。")).ToArray();
        Device? device; CancellationToken stop;
        lock (_sync)
        {
            _devices.TryGetValue(context.DeviceId, out device);
            stop = _stop?.Token ?? new CancellationToken(true);
        }
        if (device is null) return new(results);
        // 写操作同时响应调用者取消和运行停止；等待设备门时取消不会发送任何内容，已发送后的取消可能无法判断 PLC 是否接受。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
        bool entered = false;
        try
        {
            await device.Gate.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            // 每步发送前检查配置标记、连接代次、底层连接及人工重连标志；连接恢复也不会继续旧上下文的写入。
            bool Current()
            {
                var status = Volatile.Read(ref device.Status);
                return !linked.IsCancellationRequested && _planStamp == context.ConfigurationStamp &&
                    status.State == PlcDeviceState.Connected && status.ConnectionEpoch == context.ConnectionEpoch &&
                    device.Client?.ConnectionState == PlcConnectionState.Connected && !device.Reconnect;
            }
            int offset = 0;
            foreach (var step in steps)
            {
                if (!Current()) break;
                // 请求可能曾在设备队列等待：真正发送前再次核对维护锁，旧弹窗和排队写入不能绕过。
                try { foreach (var item in step) { MaintenanceInterlock.CheckWrite(item.Request.Key.LogicalId, item.Request.EngineeringValue); DebugParameterAccess.CheckWrite(item.Binding.Category, item.Request.Key.LogicalId, item.Request.EngineeringValue, context.DeviceId, context.ConnectionEpoch, context.ConfigurationStamp); } }
                catch (InvalidOperationException ex)
                {
                    for (int i = 0; i < step.Count; i++) results[offset + i] = new(step[i].Request.Key, PlcWriteState.NotSent, null, ex.Message);
                    break;
                }
                PlcBatchResult<PlcWriteResult> batch;
                try { batch = await device.Client!.WriteBatchAsync(step.Select(p => p.Transport).ToArray(), linked.Token).ConfigureAwait(false); }
                // 请求已交给通讯层后发生异常，按结果未知处理；可能已在 PLC 生效，因此只自动恢复连接，绝不重放该写入。
                catch (Exception)
                {
                    if (!stop.IsCancellationRequested)
                        SetState(device, PlcDeviceState.Faulted, "PLC 写入通讯中断，结果未知；自动重连但不重发写入。");
                    for (int i = 0; i < step.Count; i++) results[offset + i] = new(step[i].Request.Key,
                        PlcWriteState.OutcomeUnknown, null, "通讯中断或请求取消，写入结果未知；请核对PLC，不自动重发。");
                    break;
                }
                if (device.Client!.ConnectionState != PlcConnectionState.Connected || IsCommunicationError(batch.Error) ||
                    batch.Results.Any(r => IsCommunicationError(r.Error)))
                    SetState(device, PlcDeviceState.Faulted, "PLC 写入后通讯失效，将自动重连；不重发写入。");
                var current = Current();
                for (int i = 0; i < step.Count; i++)
                {
                    var item = batch.Results.ElementAtOrDefault(i);
                    var state = !current || item is null ? PlcWriteState.OutcomeUnknown : item.State;
                    results[offset + i] = new(step[i].Request.Key, state, null,
                        state == PlcWriteState.Succeeded ? "PLC服务端已接受写入；不代表动作完成。" :
                        state == PlcWriteState.OutcomeUnknown ? "结果未知，不自动重发。" : item?.Error?.Message ?? "写入未成功。");
                }
                if (batch.Error is not null && results.Skip(offset).Take(step.Count).All(r => r.State == PlcWriteState.Succeeded))
                    for (int i = 0; i < step.Count; i++) results[offset + i] = results[offset + i] with
                        { State = PlcWriteState.OutcomeUnknown, Message = "批次发生通讯错误，结果需核对；不自动重发。" };
                if (!current || !batch.AllSucceeded || batch.Results.Count != step.Count) break;
                // 回读验证只确认服务端当前值与传输值相等；不能证明机械动作完成，也不将回读失败改写为未发送。
                if (verify)
                {
                    try
                    {
                        var reads = await device.Client!.ReadBatchAsync(step.Select(p => p.Transport.Point).ToArray(), linked.Token).ConfigureAwait(false);
                        CheckReadConnection(device, reads);
                        for (int i = 0; i < step.Count; i++)
                        {
                            var read = reads.Results.ElementAtOrDefault(i);
                            bool matched = Current() && reads.Error is null && read?.Success == true && Equals(read.Value, step[i].Transport.Value);
                            results[offset + i] = results[offset + i] with { Verified = matched,
                                Message = matched ? "写入并回读一致。" : "写入已接受，回读未确认一致；请重新读取核对。" };
                        }
                    }
                    catch (Exception)
                    {
                        if (!linked.IsCancellationRequested)
                            SetState(device, PlcDeviceState.Faulted, "PLC 写入后回读通讯失败，将自动重连；不重发写入。");
                        for (int i = 0; i < step.Count; i++) results[offset + i] = results[offset + i] with
                            { Verified = false, Message = "写入已接受，但回读失败；不自动重发。" };
                    }
                    if (results.Skip(offset).Take(step.Count).Any(r => r.Verified != true)) break;
                }
                offset += step.Count;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { if (entered) device.Gate.Release(); }
        return new(results);
    }
    /// <summary>设置重连标志并唤醒工作循环；任务完成仅表示请求已提交，不代表新连接已经建立。</summary>
    internal Task ReconnectAsync(string deviceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_devices.TryGetValue(deviceId, out var device)) throw new ArgumentException("PLC 设备不存在或未启用。");
            device.Reconnect = true;
            if (device.Wake.CurrentCount == 0) device.Wake.Release();
        }
        return Task.CompletedTask;
    }
    /// <summary>取消连接循环并等待其退出，再逐设备等待在途读写、释放客户端及同步对象，最后清空运行槽位。</summary>
    internal async Task StopAsync()
    {
        _stop?.Cancel();
        _planStamp = null;
        Device[] devices; lock (_sync) devices = _devices.Values.ToArray();
        await Task.WhenAll(devices.Select(d => d.Worker)).ConfigureAwait(false);
        foreach (var device in devices)
        {
            await device.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ReleaseClientAsync(device).ConfigureAwait(false);
                SetState(device, PlcDeviceState.Disconnected);
            }
            finally { device.Gate.Release(); }
            device.Gate.Dispose(); device.Wake.Dispose();
        }
        lock (_sync) _devices.Clear();
        _stop?.Dispose(); _stop = null;
    }
}

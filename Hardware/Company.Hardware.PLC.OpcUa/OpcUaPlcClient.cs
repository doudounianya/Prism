using System.Collections.Concurrent;
using Company.Hardware.PLC.Interfaces;
using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Internal;
using Company.Hardware.PLC.OpcUa.Models;
using Company.Hardware.PLC.OpcUa.Options;
using Opc.Ua;

namespace Company.Hardware.PLC.OpcUa;

/// <summary>一个实例对应一个设备；读、写和会话切换串行，不自动采集或重放写入。</summary>
public sealed class OpcUaPlcClient : IPlcClient
{
    private readonly OpcUaConnectionOptions _options;
    private readonly OpcUaHelperSession _session;
    private readonly OpcUaNodeResolver _resolver = new();
    private readonly OpcUaReadExecutor _reader = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<PlcConnectionStateChangedEventArgs> _notifications = new();
    private int _notificationWorker;
    private int _state;
    private int _disposeRequested;
    private Task? _disposeTask;

    internal OpcUaPlcClient(OpcUaConnectionOptions options)
    {
        _options = options;
        _session = new OpcUaHelperSession(options) { Faulted = OnSessionFault };
    }

    public string DeviceId => _options.DeviceId;
    public PlcConnectionState ConnectionState => (PlcConnectionState)Volatile.Read(ref _state);
    public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ConnectionState == PlcConnectionState.Connected && _session.IsConnected) return;
            _resolver.Reset(null);
            ChangeState(PlcConnectionState.Connecting);
            try
            {
                await _session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _resolver.Reset(_session.NamespaceUris);
                _reader.Reset(Math.Min(_options.MaxNodesPerRequest, _session.MaxNodesPerRead));
                ChangeState(PlcConnectionState.Connected);
            }
            catch (Exception ex)
            {
                var error = OpcUaStatusMapper.Exception(ex, cancellationToken);
                ChangeState(PlcConnectionState.Faulted, error);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (error.Kind == PlcErrorKind.Timeout) throw new TimeoutException(error.Message);
                throw new IOException(error.Message);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ChangeState(PlcConnectionState.Disconnecting);
            await _session.DisconnectAsync().ConfigureAwait(false);
            _resolver.Reset(null);
            ChangeState(PlcConnectionState.Disconnected);
        }
        finally { _gate.Release(); }
    }

    public async Task<PlcBatchResult<PlcReadResult>> ReadBatchAsync(
        IReadOnlyList<PlcPoint> points, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ThrowIfDisposed();
        var input = points.ToArray();
        ValidatePoints(input);
        var started = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (input.Length == 0) return new([], started, DateTimeOffset.UtcNow);
            var results = new PlcReadResult[input.Length];
            var connectionError = ConnectionError();
            if (connectionError is not null)
                return new(input.Select(point => FailedRead(point, connectionError)), started, DateTimeOffset.UtcNow, connectionError);
            var resolvedPoints = new List<(NodeId Node, int? Index, int Input)>();
            for (var i = 0; i < input.Length; i++)
            {
                var point = input[i];
                if (!OpcUaValueConverter.IsSupported(point.ExpectedType))
                {
                    results[i] = FailedRead(point, new(PlcErrorKind.TypeMismatch, "不支持的 PLC 原生数据类型。"));
                    continue;
                }
                var resolved = _resolver.Resolve(point);
                if (resolved.Error is not null)
                {
                    results[i] = FailedRead(point, resolved.Error);
                    continue;
                }
                resolvedPoints.Add((resolved.Node!, ((OpcUaAddress)point.Address).ArrayIndex, i));
            }
            PlcError? batchError = null;
            try
            {
                batchError = await _reader.ExecuteAsync(OpcUaReadTarget.Build(resolvedPoints), _session.ReadAsync, (target, data) =>
                {
                    foreach (var (original, offset) in target.Inputs)
                        results[original] = MakeRead(input[original], data, target.Start is null ? null : offset, target.Length);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                batchError = OpcUaStatusMapper.Exception(ex, cancellationToken);
                if (batchError.Kind != PlcErrorKind.Cancelled) ChangeState(PlcConnectionState.Faulted, batchError);
                for (int i = 0; i < results.Length; i++)
                    results[i] ??= FailedRead(input[i], batchError);
            }
            return new(results, started, DateTimeOffset.UtcNow, batchError);
        }
        finally { _gate.Release(); }
    }

    public async Task<PlcWriteResult> WriteAsync(PlcWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WriteBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return result.Results[0];
    }

    public async Task<PlcBatchResult<PlcWriteResult>> WriteBatchAsync(
        IReadOnlyList<PlcWriteRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ThrowIfDisposed();
        var input = requests.ToArray();
        if (input.Any(request => request is null)) throw new ArgumentException("写入请求不能为空。", nameof(requests));
        ValidatePoints(input.Select(request => request.Point).ToArray());
        var started = DateTimeOffset.UtcNow;
        var errors = input.Select(request => OpcUaNodeResolver.Validate(request.Point) ?? OpcUaValueConverter.ValidateWrite(request)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Any(error => error is not null)) return RejectBatch(input, errors, started);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (input.Length == 0) return new([], started, DateTimeOffset.UtcNow);
            var connectionError = ConnectionError();
            if (connectionError is not null)
                return new(input.Select(request => NotSent(request.Point, connectionError)), started, DateTimeOffset.UtcNow, connectionError);
            var nodes = new WriteValueCollection();
            var unique = new Dictionary<(NodeId, string?), int>();
            for (var i = 0; i < input.Length; i++)
            {
                var resolved = _resolver.Resolve(input[i].Point);
                if (resolved.Error is not null) { errors[i] = resolved.Error; continue; }
                var key = (resolved.Node!, resolved.Range);
                if (!unique.TryAdd(key, i))
                    errors[i] = errors[unique[key]] = new(PlcErrorKind.InvalidRequest, "同一批不能重复写入同一个节点和数组下标。");
                nodes.Add(new WriteValue
                {
                    NodeId = resolved.Node,
                    AttributeId = Attributes.Value,
                    IndexRange = resolved.Range,
                    Value = new DataValue(new Variant(OpcUaValueConverter.WriteValue(input[i])))
                });
            }
            if (errors.Any(error => error is not null)) return RejectBatch(input, errors, started);
            var results = new PlcWriteResult[input.Length];
            PlcError? batchError = null;
            for (var offset = 0; offset < input.Length; offset += _options.MaxNodesPerRequest)
            {
                var count = Math.Min(_options.MaxNodesPerRequest, input.Length - offset);
                var sent = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = new WriteValueCollection(nodes.Skip(offset).Take(count));
                    sent = true;
                    var statuses = await _session.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < count; i++)
                    {
                        var status = statuses[i];
                        var state = StatusCode.IsGood(status) ? PlcWriteState.Succeeded :
                            StatusCode.IsBad(status) ? PlcWriteState.Rejected : PlcWriteState.OutcomeUnknown;
                        results[offset + i] = new(input[offset + i].Point, state, status.Code, DateTimeOffset.UtcNow,
                            state == PlcWriteState.Succeeded ? null : OpcUaStatusMapper.Server(status));
                    }
                }
                catch (Exception ex)
                {
                    batchError = OpcUaStatusMapper.Exception(ex, cancellationToken);
                    for (var i = offset; i < offset + count; i++)
                        results[i] = new(input[i].Point, sent ? PlcWriteState.OutcomeUnknown : PlcWriteState.NotSent,
                            batchError.NativeStatusCode, DateTimeOffset.UtcNow, batchError);
                    for (var i = offset + count; i < input.Length; i++) results[i] = NotSent(input[i].Point, batchError);
                    // 已发送但未确认的写入使本会话停止接受新操作；显式重连后恢复，禁止重放。
                    if (sent || batchError.Kind != PlcErrorKind.Cancelled) ChangeState(PlcConnectionState.Faulted, batchError);
                    break;
                }
            }
            return new(results, started, DateTimeOffset.UtcNow, batchError);
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _session.Faulted = null;
            await _session.DisconnectAsync().ConfigureAwait(false);
            _resolver.Reset(null);
            ChangeState(PlcConnectionState.Disposed);
        }
        finally { _gate.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
    private PlcError? ConnectionError() => ConnectionState == PlcConnectionState.Connected && _session.IsConnected
        ? null : new(PlcErrorKind.NotConnected, "PLC 未连接或本会话已故障，需要显式连接。");

    private static void ValidatePoints(PlcPoint[] points)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in points)
            if (point is null || string.IsNullOrWhiteSpace(point.RequestId) || !keys.Add(point.RequestId))
                throw new ArgumentException("点位不能为空，且 RequestId 在一次请求中必须非空且唯一。");
    }

    private static PlcReadResult FailedRead(PlcPoint point, PlcError error) =>
        new(point, null, null, false, PlcDataQuality.Unknown, error.NativeStatusCode, null, null, DateTimeOffset.UtcNow, error);
    private static PlcWriteResult NotSent(PlcPoint point, PlcError error) =>
        new(point, PlcWriteState.NotSent, error.NativeStatusCode, DateTimeOffset.UtcNow, error);
    private static PlcBatchResult<PlcWriteResult> RejectBatch(PlcWriteRequest[] input, PlcError?[] errors, DateTimeOffset started)
    {
        var batch = new PlcError(PlcErrorKind.InvalidRequest, "写入预检查失败，本批未发送任何写入。");
        return new(input.Select((request, index) => NotSent(request.Point, errors[index] ?? batch)), started, DateTimeOffset.UtcNow, batch);
    }

    private static PlcReadResult MakeRead(PlcPoint point, DataValue data, int? offset = null, int length = 1)
    {
        var quality = OpcUaStatusMapper.Quality(data.StatusCode);
        object? raw = data.Value;
        PlcError? shapeError = null;
        if (offset is { } element && quality != PlcDataQuality.Bad)
        {
            if (raw is not Array array || array.Rank != 1 || array.Length != length)
                shapeError = new(PlcErrorKind.InvalidResponse, "数组范围读取返回的维度或长度与申请范围不一致。");
            else raw = array.GetValue(array.GetLowerBound(0) + element);
        }
        var actualType = offset is not null && raw is Array nativeArray
            ? nativeArray.GetType().GetElementType() : raw?.GetType();
        var decoded = quality == PlcDataQuality.Bad
            ? (Value: (object?)null, Error: (PlcError?)OpcUaStatusMapper.Server(data.StatusCode))
            : shapeError is not null ? (Value: (object?)null, Error: shapeError)
            : OpcUaValueConverter.Read(raw, point.ExpectedType, false);
        return new(point, decoded.Value, actualType, decoded.Error is null, quality, data.StatusCode.Code,
            OpcUaStatusMapper.Timestamp(data.SourceTimestamp), OpcUaStatusMapper.Timestamp(data.ServerTimestamp),
            DateTimeOffset.UtcNow, decoded.Error);
    }

    private void OnSessionFault(PlcError error)
    {
        lock (_stateLock)
            if (ConnectionState == PlcConnectionState.Connected) ChangeState(PlcConnectionState.Faulted, error);
    }

    private void ChangeState(PlcConnectionState next, PlcError? error = null)
    {
        lock (_stateLock)
        {
            var previous = ConnectionState;
            if (previous == next) return;
            Volatile.Write(ref _state, (int)next);
            _notifications.Enqueue(new(previous, next, DateTimeOffset.UtcNow, error));
        }
        ScheduleNotifications();
    }

    private void ScheduleNotifications()
    {
        if (Interlocked.CompareExchange(ref _notificationWorker, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (_notifications.TryDequeue(out var notification))
                    if (ConnectionStateChanged is { } handlers)
                        foreach (EventHandler<PlcConnectionStateChangedEventArgs> handler in handlers.GetInvocationList())
                            try { handler(this, notification); }
                            catch { /* 消费者回调不能破坏会话及通讯结果。 */ }
            }
            finally
            {
                Interlocked.Exchange(ref _notificationWorker, 0);
                if (!_notifications.IsEmpty) ScheduleNotifications();
            }
        });
    }
}

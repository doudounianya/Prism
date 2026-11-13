using Company.Hardware.PLC.Models;
using Company.Hardware.PLC.OpcUa.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaHelper;

namespace Company.Hardware.PLC.OpcUa.Internal;

/// <summary>Helper 管理连接；公开 Session 提供带时间戳读取和逐节点写入状态。</summary>
internal sealed class OpcUaHelperSession(OpcUaConnectionOptions options)
{
    private OpcUaClient? _helper;
    private volatile bool _healthy;
    internal Action<PlcError>? Faulted { get; set; }
    internal bool IsConnected => _healthy && _helper?.Session?.Connected == true;
    internal NamespaceTable? NamespaceUris => _helper?.Session?.NamespaceUris;
    // 服务端未公布限制时采用已验证的保守批次，仍受调用方上限约束。
    internal int MaxNodesPerRead => _helper?.Session?.OperationLimits?.MaxNodesPerRead is > 0 and var limit
        ? (int)Math.Min(limit, int.MaxValue) : 100;

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var helper = new OpcUaClient { OpcUaName = "CRT_HMI_" + options.DeviceId, ReconnectPeriod = 0 };
        Task? pendingConnect = null;
        try
        {
            var config = helper.AppConfig;
            config.ApplicationName = helper.OpcUaName;
            config.ApplicationUri = options.ClientCertificate is { } applicationCertificate
                ? X509Utils.GetApplicationUriFromCertificate(applicationCertificate)
                : "urn:CRT_HMI:" + Uri.EscapeDataString(options.DeviceId);
            if (string.IsNullOrWhiteSpace(config.ApplicationUri))
                throw new ServiceResultException(StatusCodes.BadCertificateUriInvalid, "应用证书缺少应用 URI。");
            config.TransportQuotas.OperationTimeout = options.ConnectionTimeoutMs;
            config.ClientConfiguration.DefaultSessionTimeout = 60000;
            // 替换 Helper 自带的接受未信任证书处理器，不修改第三方源码。
            var storeRoot = Path.GetFullPath(options.CertificateStorePath);
            config.SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
                SuppressNonceValidationErrors = false,
                ApplicationCertificate = options.ClientCertificate is { } certificate
                    ? new CertificateIdentifier(certificate)
                    : new CertificateIdentifier { StoreType = "Directory", StorePath = Path.Combine(storeRoot, "own"), SubjectName = helper.OpcUaName },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(storeRoot, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(storeRoot, "issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(storeRoot, "rejected") }
            };
            config.CertificateValidator = new CertificateValidator();
            await config.CertificateValidator.Update(config.SecurityConfiguration).ConfigureAwait(false);
            helper.UseSecurity = options.SecurityMode != OpcUaSecurityMode.None;
            helper.UserIdentity = options.UserCertificate is { } identityCertificate ? new UserIdentity(identityCertificate) :
                string.IsNullOrEmpty(options.UserName) ? new UserIdentity(new AnonymousIdentityToken()) :
                new UserIdentity(options.UserName, options.Password ?? "");
            // Helper 的端点发现含同步调用，放到后台；取消后迟到会话只关闭，不再发布给客户端。
            cancellationToken.ThrowIfCancellationRequested();
            pendingConnect = Task.Run(() => helper.ConnectServer(options.EndpointUrl), CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectionTimeoutMs);
            await pendingConnect.WaitAsync(timeout.Token).ConfigureAwait(false);
            var session = helper.Session ?? throw new InvalidOperationException("未建立 OPC UA 会话。");
            var mode = options.SecurityMode switch
            {
                OpcUaSecurityMode.Sign => MessageSecurityMode.Sign,
                OpcUaSecurityMode.SignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
                _ => MessageSecurityMode.None
            };
            // Helper 选择端点后严格核对，不能静默使用不同的安全模式或策略。
            if (session.Endpoint.SecurityMode != mode || session.Endpoint.SecurityPolicyUri != options.SecurityPolicyUri)
                throw new ServiceResultException(StatusCodes.BadSecurityPolicyRejected, "服务端所选端点与配置的安全模式或策略不一致。");
            session.OperationTimeout = options.OperationTimeoutMs;
            session.KeepAlive += OnKeepAlive;
            _helper = helper;
            _healthy = true;
        }
        catch
        {
            if (pendingConnect is { IsCompleted: false })
            {
                _ = pendingConnect.ContinueWith(task =>
                {
                    _ = task.Exception; // 观察迟到连接的异常。
                    Close(helper);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            else
            {
                _ = pendingConnect?.Exception;
                Close(helper);
            }
            throw;
        }
    }

    internal async Task DisconnectAsync()
    {
        var helper = _helper;
        _helper = null;
        _healthy = false;
        if (helper is null) return;
        if (helper.Session is { } session) session.KeepAlive -= OnKeepAlive;
        await Task.Run(() => Close(helper)).ConfigureAwait(false);
    }

    private static void Close(OpcUaClient helper)
    {
        var session = helper.Session;
        try { helper.Disconnect(); }
        catch { /* 释放失败不能令迟到连接任务产生未观察异常。 */ }
        finally
        {
            try { session?.Dispose(); }
            catch { /* Disconnect 失败时仍尝试释放会话。 */ }
        }
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs args)
    {
        if (!ReferenceEquals(_helper?.Session, session) || !ServiceResult.IsBad(args.Status)) return;
        _healthy = false;
        Faulted?.Invoke(new(PlcErrorKind.Communication, "OPC UA 会话保活失败。", args.Status.StatusCode.Code));
    }

    internal async Task<DataValueCollection> ReadAsync(ReadValueIdCollection nodes, CancellationToken cancellationToken)
    {
        var session = _helper?.Session ?? throw new InvalidOperationException("OPC UA 未连接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OperationTimeoutMs);
        try
        {
            var response = await session.ReadAsync(new RequestHeader { TimeoutHint = (uint)options.OperationTimeoutMs },
                0, TimestampsToReturn.Both, nodes, timeout.Token).ConfigureAwait(false);
            ClientBase.ValidateResponse(response.Results, nodes);
            ClientBase.ValidateDiagnosticInfos(response.DiagnosticInfos, nodes);
            return response.Results;
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("OPC UA 读取等待结束。", ex, timeout.Token);
        }
    }

    internal async Task<StatusCodeCollection> WriteAsync(WriteValueCollection nodes, CancellationToken cancellationToken)
    {
        var session = _helper?.Session ?? throw new InvalidOperationException("OPC UA 未连接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OperationTimeoutMs);
        try
        {
            var response = await session.WriteAsync(new RequestHeader { TimeoutHint = (uint)options.OperationTimeoutMs },
                nodes, timeout.Token).ConfigureAwait(false);
            ClientBase.ValidateResponse(response.Results, nodes);
            ClientBase.ValidateDiagnosticInfos(response.DiagnosticInfos, nodes);
            return response.Results;
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("OPC UA 写入等待结束。", ex, timeout.Token);
        }
    }
}

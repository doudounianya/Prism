using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace Company.Hardware.PLC.OpcUa.Options;

public enum OpcUaSecurityMode { None, Sign, SignAndEncrypt }

/// <summary>运行时连接参数。凭据由应用层提供，不从本类自行读取 Excel，也不记录密码。</summary>
public sealed class OpcUaConnectionOptions
{
    public required string DeviceId { get; init; }
    public required string EndpointUrl { get; init; }
    public OpcUaSecurityMode SecurityMode { get; init; } = OpcUaSecurityMode.None;
    public string SecurityPolicyUri { get; init; } = "http://opcfoundation.org/UA/SecurityPolicy#None";
    public string? UserName { get; init; }
    [JsonIgnore]
    public string? Password { get; init; }
    /// <summary>调用方持有证书所有权；本客户端不会释放调用方传入的证书。</summary>
    [JsonIgnore]
    public X509Certificate2? ClientCertificate { get; init; }
    /// <summary>可选的用户证书身份，与 UserName 二选一；不等同于应用证书。</summary>
    [JsonIgnore]
    public X509Certificate2? UserCertificate { get; init; }
    public string CertificateStorePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Config", "PLC", "pki");
    public int ConnectionTimeoutMs { get; init; } = 10000;
    public int OperationTimeoutMs { get; init; } = 5000;
    public int MaxNodesPerRequest { get; init; } = 500;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new ArgumentException("设备编号不能为空。", nameof(DeviceId));
        if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "opc.tcp" || string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("需要不包含身份凭据的 opc.tcp 端点。", nameof(EndpointUrl));
        if (!Enum.IsDefined(SecurityMode) || string.IsNullOrWhiteSpace(SecurityPolicyUri))
            throw new ArgumentException("安全模式或策略无效。");
        var none = "http://opcfoundation.org/UA/SecurityPolicy#None";
        if ((SecurityMode == OpcUaSecurityMode.None) != (SecurityPolicyUri == none))
            throw new ArgumentException("安全模式和安全策略不一致。");
        if (ConnectionTimeoutMs <= 0 || OperationTimeoutMs <= 0 || MaxNodesPerRequest <= 0)
            throw new ArgumentException("超时和单次节点上限必须为正整数。");
        if (SecurityMode != OpcUaSecurityMode.None && ClientCertificate is null)
            throw new ArgumentException("安全连接需要提供应用证书。", nameof(ClientCertificate));
        if (ClientCertificate is not null && !ClientCertificate.HasPrivateKey)
            throw new ArgumentException("应用证书必须包含私钥。", nameof(ClientCertificate));
        if (UserCertificate is not null && !UserCertificate.HasPrivateKey)
            throw new ArgumentException("用户证书必须包含私钥。", nameof(UserCertificate));
        if (UserCertificate is not null && !string.IsNullOrEmpty(UserName))
            throw new ArgumentException("用户证书身份和用户名身份不能同时指定。");
        if (Password is not null && string.IsNullOrWhiteSpace(UserName))
            throw new ArgumentException("密码必须配合用户名使用。");
        if (string.IsNullOrWhiteSpace(CertificateStorePath))
            throw new ArgumentException("证书存储目录不能为空。");
    }
}

using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
namespace Company.Application.Share.PLC.Runtime.Models;

/// <summary>引用本地身份，非 Excel 配置模型。无自动生成的含密 ToString，证书由提供器管理。</summary>
/// <remarks>连接工厂仅借用证书对象；敏感字段排除 JSON 序列化，调用方也不得将此对象展开写入日志。</remarks>
public sealed class PlcConnectionProfile
{
    /// <summary>可选用户身份名称；为空时由底层连接身份策略处理。</summary>
    public string? UserName { get; init; }
    /// <summary>与用户身份对应的敏感密码，仅供建立连接使用。</summary>
    [JsonIgnore] public string? Password { get; init; }
    /// <summary>客户端应用证书，所有权仍在提供方，必须覆盖使用该证书的客户端生命周期。</summary>
    [JsonIgnore] public X509Certificate2? ApplicationCertificate { get; init; }
    /// <summary>可选用户证书，与应用证书的身份用途区分。</summary>
    [JsonIgnore] public X509Certificate2? UserCertificate { get; init; }
    /// <summary>本地 OPC UA 证书库路径；为空时由客户端工厂提供应用默认路径。</summary>
    public string? CertificateStorePath { get; init; }
}

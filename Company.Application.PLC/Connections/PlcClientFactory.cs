using System.IO;
using Company.Application.PLC.Options;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;
using Company.Hardware.PLC.Interfaces;
using Company.Hardware.PLC.OpcUa;
using Company.Hardware.PLC.OpcUa.Options;

namespace Company.Application.PLC.Connections;

/// <summary>将设备连接参数和本地身份组合成 OPC UA 客户端；仅创建实例，连接与释放由连接管理器负责。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcClientFactory(PlcRuntimeOptions options)
{
    internal IPlcClient Create(PlcDeviceDefinition device, PlcConnectionProfile profile)
    {
        var mode = device.SecurityMode switch
        {
            "None" => OpcUaSecurityMode.None, "Sign" => OpcUaSecurityMode.Sign,
            "SignAndEncrypt" => OpcUaSecurityMode.SignAndEncrypt,
            _ => throw new ArgumentException("不支持的 OPC UA 安全模式。")
        };
        // 兼容完整策略 URI 与短名称；不将用户名、密码或证书内容拼接到端点字符串中。
        var policy = device.SecurityPolicy.StartsWith("http://", StringComparison.Ordinal)
            ? device.SecurityPolicy : "http://opcfoundation.org/UA/SecurityPolicy#" + device.SecurityPolicy;
        return new OpcUaPlcClientFactory().Create(new()
        {
            DeviceId = device.Id, EndpointUrl = device.OpcUaEndpoint, SecurityMode = mode, SecurityPolicyUri = policy,
            // 连接超时取设备配置，缺省为 10000 毫秒；单次操作超时与批量节点上限由运行选项传入通讯层。
            ConnectionTimeoutMs = device.ConnectionTimeoutMs ?? 10000, OperationTimeoutMs = options.OperationTimeoutMs,
            MaxNodesPerRequest = options.MaxNodesPerRequest, UserName = profile.UserName, Password = profile.Password,
            ClientCertificate = profile.ApplicationCertificate, UserCertificate = profile.UserCertificate,
            CertificateStorePath = profile.CertificateStorePath ?? Path.Combine(AppContext.BaseDirectory, "Config", "PLC", "pki")
        });
    }
}

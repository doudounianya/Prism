using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
namespace Company.PlcHmi.Shared.PLC.Runtime;

/// <summary>从设备的身份引用解析本地用户名和证书等连接资料，与不含密码的 Excel 配置隔离。</summary>
public interface IPlcConnectionProfileProvider
{
    /// <summary>提供器保有证书所有权，须在使用它的客户端释放后才释放证书。</summary>
    ValueTask<PlcConnectionProfile> ResolveAsync(PlcDeviceDefinition device, CancellationToken cancellationToken = default);
}

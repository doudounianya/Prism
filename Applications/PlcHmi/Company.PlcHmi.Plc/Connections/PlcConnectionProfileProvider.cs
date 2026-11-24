using System.Collections.Concurrent;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.PlcHmi.Plc.Connections;

/// <summary>本批提供显式注入身份的入口，不创建密码文件或凭据管理界面。</summary>
/// <remarks>引用名按序号区分大小写；身份仅保存在进程内，解析失败不会退回匿名安全连接。</remarks>
[ExposedService(Lifetime.Singleton, typeof(IPlcConnectionProfileProvider))]
public sealed class PlcConnectionProfileProvider : IPlcConnectionProfileProvider
{
    private readonly ConcurrentDictionary<string, PlcConnectionProfile> profiles = new(StringComparer.Ordinal);
    /// <summary>按引用名设置或替换本地身份；客户端使用的证书生命周期仍由身份提供方管理。</summary>
    public void SetProfile(string reference, PlcConnectionProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(profile);
        profiles[reference] = profile;
    }
    public ValueTask<PlcConnectionProfile> ResolveAsync(PlcDeviceDefinition device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 显式引用必须已注入；无引用时仅允许 None 模式使用空身份，防止静默降低身份要求。
        if (!string.IsNullOrWhiteSpace(device.CredentialReference))
            return ValueTask.FromResult(profiles.TryGetValue(device.CredentialReference, out var profile)
                ? profile : throw new InvalidOperationException("PLC 凭据引用尚未配置。"));
        if (device.SecurityMode != "None") throw new InvalidOperationException("安全连接需要显式配置身份与应用证书。");
        return ValueTask.FromResult(new PlcConnectionProfile());
    }
}

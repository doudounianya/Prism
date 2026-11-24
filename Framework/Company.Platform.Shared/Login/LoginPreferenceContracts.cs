using Company.Platform.Shared.Model;

namespace Company.Platform.Shared.Login;

/// <summary>仅用于还原表单的短期凭据；恢复偏好不代表已认证会话。禁止在日志输出密码。</summary>
public sealed record LoginPreferenceState(string UserName, string Password, bool Remember, bool AutoLogin, string? Notice)
{
    public override string ToString() => $"LoginPreferenceState {{ Remember = {Remember}, AutoLogin = {AutoLogin}, Credentials = Hidden }}";
}

public interface IRememberedLoginStore
{
    LoginPreferenceState Load();
    void Save(CurrentUser user, bool remember, bool autoLogin);
}

/// <summary>操作系统凭据保护边界；公共服务不直接调用 Windows DPAPI。</summary>
public interface ILoginSecretProtector
{
    string Protect(string userName, string password, string purpose);
    string Unprotect(string userName, string protectedPassword, string purpose);
}

/// <summary>应用必须明确提供保护用途；既有软件升级时必须保持旧用途不变才能恢复原密文。</summary>
public sealed record LoginPreferenceOptions
{
    public string ProtectionPurpose { get; }
    public string DefaultUserName { get; }
    public LoginPreferenceOptions(string protectionPurpose,string defaultUserName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionPurpose);
        ArgumentNullException.ThrowIfNull(defaultUserName);
        ProtectionPurpose=protectionPurpose;DefaultUserName=defaultUserName;
    }
}

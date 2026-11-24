using Company.Platform.Shared.Model;
using Company.Core.Cache;
using Newtonsoft.Json.Linq;
using System.Security;
using System.Security.Cryptography;
using Company.Platform.Shared.Login;
using Company.Core.Ioc;

namespace Company.Platform.Services.Login;

/// <summary>使用应用提供的凭据保护器保存登录偏好，兼容并清理旧版明文缓存；不建立认证会话。</summary>
[ExposedService(Lifetime.Singleton, typeof(IRememberedLoginStore))]
public sealed class RememberedLoginStore(ICacheManager cache, ILoginSecretProtector protector, LoginPreferenceOptions options) : IRememberedLoginStore
{
    private const int ProtectionVersion = 1;
    private const string CacheNotice = "登录偏好无法安全恢复，请重新输入密码；自动登录已关闭。";
    private const string SaveNotice = "身份验证已通过，但登录偏好未能安全保存；下次启动请重新输入密码。";

    /// <summary>迁移成功前不返回旧明文；密文损坏时也不尝试读取旧 Password 字段。</summary>
    public LoginPreferenceState Load()
    {
        string userName = options.DefaultUserName;
        try
        {
            bool remember = cache.Get(CacheKey.IsRemember, out bool remembered) && remembered;
            bool autoLogin = cache.Get(CacheKey.IsAutoLogin, out bool automatic) && automatic;

            // 读取 JSON 形状以区分旧记录与未知版本，不能用 CurrentUser 反序列化丢失版本标记。
            if (!cache.Get(CacheKey.User, out JToken cached) || cached is null)
            {
                // 主文件可能已删除用户项而上次删除尚未轮换备份，仍强制写入安全标记。
                RemoveCredentials();
                return new(userName, "", false, false, null);
            }
            if (cached is not JObject entry)
                return CannotRestore(userName);

            if (!TryReadString(entry, nameof(ProtectedLoginEntry.UserName), out string name) ||
                string.IsNullOrWhiteSpace(name))
                return CannotRestore(userName);
            userName = name;

            if (!remember)
            {
                RemoveCredentials();
                return new(userName, "", false, false, null);
            }

            bool hasVersion = entry.TryGetValue(nameof(ProtectedLoginEntry.ProtectionVersion),
                StringComparison.OrdinalIgnoreCase, out JToken? version);
            bool hasCiphertext = entry.TryGetValue(nameof(ProtectedLoginEntry.ProtectedPassword),
                StringComparison.OrdinalIgnoreCase, out _);

            if (hasVersion || hasCiphertext)
            {
                // 任意保护标记存在时，都必须按完整的当前密文格式处理；禁止明文回退。
                if (!hasVersion || !hasCiphertext || version?.Type != JTokenType.Integer ||
                    version.Value<long>() != ProtectionVersion || string.IsNullOrWhiteSpace(userName) ||
                    !TryReadString(entry, nameof(ProtectedLoginEntry.ProtectedPassword), out string protectedPassword) ||
                    string.IsNullOrEmpty(protectedPassword))
                    return CannotRestore(userName);

                string password = protector.Unprotect(userName, protectedPassword, options.ProtectionPurpose);
                if (string.IsNullOrEmpty(password)) return CannotRestore(userName);

                // 同样轮换已保护记录，完成上次可能只写入了一次的迁移，并清除未知的旧备份。
                PersistProtected(new ProtectedLoginEntry(userName, protectedPassword), autoLogin);
                return new(userName, password, true, autoLogin, null);
            }

            if (string.IsNullOrWhiteSpace(userName) ||
                !TryReadString(entry, nameof(CurrentUser.Password), out string legacyPassword) ||
                string.IsNullOrEmpty(legacyPassword))
                return CannotRestore(userName);

            var migrated = new ProtectedLoginEntry(userName, protector.Protect(userName, legacyPassword, options.ProtectionPurpose));
            PersistProtected(migrated, autoLogin);
            return new(userName, legacyPassword, true, autoLogin, null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // 不记录异常、缓存 JSON 或密码；缓存故障不能阻止手工输入和正常认证。
            return CannotRestore(userName);
        }
    }

    /// <summary>仅保存用户名与受保护密码；不把 CurrentUser 的身份、角色或明文写入缓存。</summary>
    public void Save(CurrentUser user, bool remember, bool autoLogin)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!remember || string.IsNullOrWhiteSpace(user.UserName) || string.IsNullOrEmpty(user.Password))
        {
            RemoveCredentials();
            return;
        }

        ProtectedLoginEntry entry;
        try
        {
            entry = new ProtectedLoginEntry(user.UserName, protector.Protect(user.UserName, user.Password, options.ProtectionPurpose));
        }
        catch (Exception exception) when (exception is CryptographicException or NotSupportedException or SecurityException)
        {
            TryRemoveCredentials();
            // 不附带 DPAPI 异常上下文，调用方可以安全显示或记录这条通用提示。
            throw new InvalidOperationException(SaveNotice);
        }
        PersistProtected(entry, autoLogin);
    }

    private void PersistProtected(ProtectedLoginEntry entry, bool autoLogin)
    {
        // 先替换用户项，避免更新标记时再次持久化旧明文；自动登录最后才启用。
        cache.Set(CacheKey.User, entry);
        cache.Set(CacheKey.IsAutoLogin, false);
        // 连续轮换安全内容，让正常主文件及 .bak 都不再包含上次凭据。
        cache.Set(CacheKey.User, entry);
        cache.Set(CacheKey.IsRemember, true);
        cache.Set(CacheKey.IsAutoLogin, autoLogin);
    }

    private void RemoveCredentials()
    {
        cache.Delete(CacheKey.User);
        // Delete 在键不存在时不会写文件；两个标记仍强制写入，轮换掉旧的凭据备份。
        cache.Set(CacheKey.IsRemember, false);
        cache.Set(CacheKey.IsAutoLogin, false);
    }

    private LoginPreferenceState CannotRestore(string userName)
    {
        TryRemoveCredentials();
        return new(userName, "", false, false, CacheNotice);
    }

    private void TryRemoveCredentials()
    {
        try { RemoveCredentials(); }
        catch (Exception exception) when (IsRecoverable(exception)) { /* 登录缓存清理为尽力操作。 */ }
    }

    private static bool TryReadString(JObject entry, string propertyName, out string value)
    {
        value = "";
        if (!entry.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken? token) ||
            token.Type != JTokenType.String)
            return false;
        value = token.Value<string>() ?? "";
        return true;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    // UserName 名称保持兼容，既有账号服务可继续 Get<CurrentUser> 后按用户名失效缓存。
    private sealed class ProtectedLoginEntry(string userName, string protectedPassword)
    {
        public string UserName { get; } = userName;
        public int ProtectionVersion { get; } = RememberedLoginStore.ProtectionVersion;
        public string ProtectedPassword { get; } = protectedPassword;
    }
}

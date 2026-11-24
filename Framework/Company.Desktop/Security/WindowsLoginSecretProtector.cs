using Company.Platform.Shared.Login;
using System.Security.Cryptography;
using System.Text;

namespace Company.Desktop.Security;

/// <summary>保持既有 CurrentUser DPAPI 与用途/用户名熵算法；仅 Windows 桌面层依赖操作系统保护。</summary>
public sealed class WindowsLoginSecretProtector : ILoginSecretProtector
{
    public string Protect(string userName, string password, string purpose)
    {
        byte[]? plaintext = null, entropy = null, ciphertext = null;
        try
        {
            plaintext = Encoding.UTF8.GetBytes(password);
            entropy = Encoding.UTF8.GetBytes(purpose + userName);
            ciphertext = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(ciphertext);
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(ciphertext);
        }
    }

    public string Unprotect(string userName, string protectedPassword, string purpose)
    {
        byte[]? plaintext = null, entropy = null, ciphertext = null;
        try
        {
            ciphertext = Convert.FromBase64String(protectedPassword);
            entropy = Encoding.UTF8.GetBytes(purpose + userName);
            plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(ciphertext);
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }

}

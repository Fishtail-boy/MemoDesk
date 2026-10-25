using System.Security.Cryptography;
using System.Text;
using MemoDesk.Core.Settings;

namespace MemoDesk.Platform.Windows.Settings;

/// <summary>
/// Windows DPAPI（当前用户范围）保护敏感设置，API Key 不以明文落库（设计 6.1 / 7.7）。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MemoDesk.Settings.v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var data = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        var encrypted = Convert.FromBase64String(protectedValue);
        var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}

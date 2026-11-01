using System.Security.Cryptography;

namespace MemoDesk.Vault.Core;

public static class PasswordGenerator
{
    public static string Generate(int length = 24, bool upper = true, bool lower = true, bool digits = true, bool symbols = true)
    {
        if (length is < 16 or > 64) throw new VaultException(VaultError.InvalidData);
        var sets = new List<string>();
        if (upper) sets.Add("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (lower) sets.Add("abcdefghijklmnopqrstuvwxyz");
        if (digits) sets.Add("0123456789");
        if (symbols) sets.Add("!@#$%^&*()-_=+[]{}:,.?");
        if (sets.Count == 0) throw new VaultException(VaultError.InvalidData);
        var alphabet = string.Concat(sets);
        var chars = new char[length];
        try
        {
            do
            {
                for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            } while (sets.Any(set => !chars.Any(set.Contains)));
            return new string(chars);
        }
        finally { Array.Clear(chars); }
    }
}

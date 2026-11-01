// Adapted from LockerIt AesGcmVaultCipher, commit 47658a1761a4b4c144bbdffac6fb1cc964bb3f38.
// Copyright (c) 2026 LockerIt contributors. MIT; see third-party/LockerIt-LICENSE.txt.
// MemoDesk: binary payloads, bounded parsing, identity/version AAD, no secret-bearing errors.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoDesk.Vault.Core;

namespace MemoDesk.Vault.Infrastructure;

internal sealed record VaultEnvelope(int Format, Guid VaultId, int KeyVersion, int Iterations, byte[] Salt, byte[] WrappedKey);
internal sealed record StoredEntry(Guid Id, long Version, byte[] Metadata, byte[] Secret);
internal sealed class SecretData
{
    public string Password { get; set; } = "";
    public string Notes { get; set; } = "";
}

internal static class VaultCrypto
{
    public const int Iterations = 600_000;
    public const int MaxPayload = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static void ValidatePassword(string password)
    {
        if (MasterPasswordPolicy.GetError(password, creating: true) is not null) throw new VaultException(VaultError.InvalidPasswordFormat);
    }
    public static VaultEnvelope Wrap(Guid id, int keyVersion, byte[] key, string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(32);
        var envelope = new VaultEnvelope(1, id, keyVersion, Iterations, salt, []);
        var kek = Derive(password, salt, Iterations);
        try { return envelope with { WrappedKey = EncryptBytes(key, kek, EnvelopeAad(envelope)) }; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }
    public static byte[] Unwrap(VaultEnvelope envelope, string password)
    {
        if (envelope.Format != 1 || envelope.VaultId == Guid.Empty || envelope.KeyVersion != 1 ||
            envelope.Iterations is < Iterations or > 5_000_000 || envelope.Salt is not { Length: 32 } ||
            envelope.WrappedKey is not { Length: 61 }) throw new VaultException(VaultError.InvalidData);
        if (MasterPasswordPolicy.GetError(password, creating: false) is not null) throw new VaultException(VaultError.InvalidPasswordFormat);
        var kek = Derive(password, envelope.Salt, envelope.Iterations);
        try
        {
            var key = DecryptBytes(envelope.WrappedKey, kek, EnvelopeAad(envelope));
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new VaultException(VaultError.InvalidData); }
            return key;
        }
        catch (CryptographicException) { throw new VaultException(VaultError.InvalidPassword); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }
    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var bytes = Utf8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static byte[] Encrypt<T>(T value, byte[] key, byte[] aad)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (plaintext.Length > MaxPayload) throw new VaultException(VaultError.TooLarge);
            return EncryptBytes(plaintext, key, aad);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static T Decrypt<T>(byte[] data, byte[] key, byte[] aad)
    {
        var plaintext = DecryptBytes(data, key, aad);
        try { return JsonSerializer.Deserialize<T>(plaintext) ?? throw new VaultException(VaultError.InvalidData); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static byte[] EncryptBytes(byte[] plaintext, byte[] key, byte[] aad)
    {
        var packed = new byte[29 + plaintext.Length];
        packed[0] = 1;
        RandomNumberGenerator.Fill(packed.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(packed.AsSpan(1, 12), plaintext, packed.AsSpan(29), packed.AsSpan(13, 16), aad);
        return packed;
    }
    private static byte[] DecryptBytes(byte[] packed, byte[] key, byte[] aad)
    {
        if (packed.Length is < 29 or > MaxPayload + 29 || packed[0] != 1) throw new VaultException(VaultError.InvalidData);
        var plaintext = new byte[packed.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(packed.AsSpan(1, 12), packed.AsSpan(29), packed.AsSpan(13, 16), plaintext, aad);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }
    private static byte[] EnvelopeAad(VaultEnvelope e)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("MemoDesk.Vault.Key"); w.Write(e.Format); w.Write(e.VaultId.ToByteArray());
        w.Write(e.KeyVersion); w.Write(e.Iterations); w.Write(e.Salt.Length); w.Write(e.Salt);
        return stream.ToArray();
    }
    public static byte[] EntryAad(VaultEnvelope e, Guid id, long version, bool secret)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("MemoDesk.Vault.Entry"); w.Write(e.Format); w.Write(e.VaultId.ToByteArray());
        w.Write(e.KeyVersion); w.Write(id.ToByteArray()); w.Write(version); w.Write(secret);
        return stream.ToArray();
    }
    public static byte[] BackupKey(byte[] key, Guid vaultId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, vaultId.ToByteArray(), "MemoDesk.Vault.Backup.v1"u8.ToArray());
}

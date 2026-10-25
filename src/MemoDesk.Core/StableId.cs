using System.Security.Cryptography;
using System.Text;

namespace MemoDesk.Core;

/// <summary>由业务键生成稳定 UUID；用于同一原文重试/重复回调的幂等 ID。</summary>
public static class StableId
{
    public static Guid Create(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var text = string.Join('\u001f', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static Guid Candidate(Guid captureId, int operationVersion, int index) =>
        Create("candidate", captureId.ToString("N"), operationVersion.ToString(), index.ToString());

    public static Guid ExtraMemo(Guid captureId, int operationVersion, int index) =>
        Create("memo", captureId.ToString("N"), operationVersion.ToString(), index.ToString());
}

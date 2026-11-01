namespace MemoDesk.Vault.Core;

/// <summary>创建/修改主密码的共同规则；校验信息只包含长度，不包含密码内容。</summary>
public static class MasterPasswordPolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 1024;
    public const string Requirements = "12–1024 个字符，支持中文和空格；不要求特定字符组合。";

    public static string? GetError(string? password, bool creating)
    {
        var length = password?.Length ?? 0;
        if (length == 0) return "请输入主密码。";
        if (creating && length < MinimumLength)
            return $"主密码至少需要 {MinimumLength} 个字符，目前只有 {length} 个，还差 {MinimumLength - length} 个。";
        if (length > MaximumLength)
            return $"主密码最多支持 {MaximumLength} 个字符，目前为 {length} 个，请减少 {length - MaximumLength} 个。";
        return null;
    }
}

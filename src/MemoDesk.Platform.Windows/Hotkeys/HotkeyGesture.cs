using MemoDesk.Platform.Windows.Interop;

namespace MemoDesk.Platform.Windows.Hotkeys;

/// <summary>解析后的热键组合（Win32 修饰符 + 虚拟键码）。</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = CreateNamedKeys();

    /// <summary>解析形如 "Ctrl+Alt+Space" 的快捷键文本。</summary>
    public static HotkeyGesture Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new FormatException($"快捷键格式无效：{text}。示例：Ctrl+Alt+Space。");
        }

        uint modifiers = 0;
        uint virtualKey = 0;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (virtualKey != 0)
                    {
                        throw new FormatException($"快捷键只能包含一个主键：{text}。");
                    }

                    virtualKey = ResolveVirtualKey(part, text);
                    break;
            }
        }

        if (virtualKey == 0)
        {
            throw new FormatException($"快捷键缺少主键：{text}。示例：Ctrl+Alt+Space。");
        }

        if (modifiers == 0)
        {
            throw new FormatException($"快捷键至少需要一个修饰键（Ctrl/Alt/Shift/Win）：{text}。");
        }

        return new HotkeyGesture(modifiers, virtualKey);
    }

    private static uint ResolveVirtualKey(string token, string original)
    {
        var upper = token.ToUpperInvariant();
        if (NamedKeys.TryGetValue(upper, out var key))
        {
            return key;
        }

        if (upper.Length == 1)
        {
            var character = upper[0];
            if (character is >= 'A' and <= 'Z')
            {
                return character; // VK_A..VK_Z 与 ASCII 字母一致
            }

            if (character is >= '0' and <= '9')
            {
                return character; // VK_0..VK_9 与 ASCII 数字一致
            }
        }

        throw new FormatException($"无法识别的快捷键按键：{token}（快捷键 {original}）。");
    }

    /// <summary>把 Win32 虚拟键与修饰符格式化回稳定的展示文本。</summary>
    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");

        parts.Add(FormatVirtualKey(virtualKey));
        return string.Join('+', parts);
    }

    public static bool IsModifierVirtualKey(uint virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        foreach (var pair in NamedKeys)
        {
            if (pair.Value == virtualKey && pair.Key is not ("RETURN" or "ESCAPE"))
            {
                return TitleCaseKey(pair.Key);
            }
        }

        return $"VK{virtualKey:X2}";
    }

    private static string TitleCaseKey(string key) => key switch
    {
        "PAGEUP" => "PageUp",
        "PAGEDOWN" => "PageDown",
        "BACKSPACE" => "Backspace",
        "DELETE" => "Delete",
        "INSERT" => "Insert",
        "SPACE" => "Space",
        "ENTER" => "Enter",
        "TAB" => "Tab",
        "HOME" => "Home",
        "END" => "End",
        "UP" => "Up",
        "DOWN" => "Down",
        "LEFT" => "Left",
        "RIGHT" => "Right",
        _ => key.Length > 1 ? char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant() : key,
    };

    private static Dictionary<string, uint> CreateNamedKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D,
            ["RETURN"] = 0x0D,
            ["TAB"] = 0x09,
            ["ESC"] = 0x1B,
            ["ESCAPE"] = 0x1B,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["UP"] = 0x26,
            ["DOWN"] = 0x28,
            ["LEFT"] = 0x25,
            ["RIGHT"] = 0x27,
        };

        for (var i = 1; i <= 24; i++)
        {
            keys[$"F{i}"] = (uint)(0x70 + i - 1);
        }

        return keys;
    }
}

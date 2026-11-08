namespace VoxShift.Core.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,      // MOD_ALT
    Control = 2,  // MOD_CONTROL
    Shift = 4,    // MOD_SHIFT
    Windows = 8,  // MOD_WIN
}

/// <summary>
/// A global hotkey such as "Ctrl+Alt+M". The key part is the name of a WPF <c>System.Windows.Input.Key</c>
/// value (kept as a string here so this assembly has no UI dependency). Modifier flag values match the
/// Win32 RegisterHotKey MOD_* constants.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public bool IsValid => Modifiers != HotkeyModifiers.None && !string.IsNullOrWhiteSpace(Key) && !IsModifierKey(Key);

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        if (!string.IsNullOrWhiteSpace(Key)) parts.Add(DisplayKey(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    mods |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    mods |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "windows":
                    mods |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (key is not null) return false; // two non-modifier keys
                    key = ParseKey(raw);
                    break;
            }
        }

        if (key is null) return false;
        gesture = new HotkeyGesture(mods, key);
        return gesture.IsValid;
    }

    private static string ParseKey(string raw)
    {
        // Single digits are named D0..D9 in WPF's Key enum.
        if (raw.Length == 1 && char.IsDigit(raw[0])) return "D" + raw;
        if (raw.Length == 1 && char.IsLetter(raw[0])) return raw.ToUpperInvariant();
        return raw;
    }

    private static string DisplayKey(string key) =>
        key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1].ToString() : key;

    private static bool IsModifierKey(string key) => key switch
    {
        "LeftCtrl" or "RightCtrl" or "LeftAlt" or "RightAlt" or "LeftShift" or "RightShift" or "LWin" or "RWin" or "System" => true,
        _ => false,
    };
}

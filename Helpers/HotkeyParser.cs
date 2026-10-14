using System.Windows.Input;

namespace ClickyWindows.Helpers;

/// <summary>Parses "Ctrl+Shift+Space"-style text into RegisterHotKey modifiers and a virtual key.</summary>
internal static class HotkeyParser
{
    public const string Default = "Ctrl+Shift+Space";

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "win" or "windows": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (virtualKey != 0 || !Enum.TryParse<Key>(raw, ignoreCase: true, out var key)) return false;
                    virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        // RegisterHotKey needs a non-modifier key; a bare or Shift-only key would hijack normal typing
        return virtualKey != 0 && modifiers != 0 && modifiers != Win32.MOD_SHIFT;
    }

    /// <summary>"Ctrl+Shift+Space"-style text for a key press; null while only modifiers are held.</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System) return null;

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Parses <paramref name="text"/>, falling back to Ctrl+Shift+Space when it is invalid.</summary>
    public static (uint modifiers, uint virtualKey, string text) ParseOrDefault(string? text)
    {
        if (TryParse(text, out var mods, out var vk)) return (mods, vk, text!.Trim());
        Logger.Error($"[Hotkey] \"{text}\" is not a valid hotkey — using {Default}");
        TryParse(Default, out mods, out vk);
        return (mods, vk, Default);
    }
}

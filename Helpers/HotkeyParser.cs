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
        // RegisterHotKey needs a non-modifier key; a bare letter would hijack normal typing
        return virtualKey != 0 && modifiers != 0;
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

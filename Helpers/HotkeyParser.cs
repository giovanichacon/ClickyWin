using System.Windows.Input;

namespace ClickyWindows.Helpers;

/// <summary>A shortcut: modifier flags (Win32 MOD_*) plus an optional key (0 = modifiers only, e.g. Ctrl+Win).</summary>
public readonly record struct Chord(uint Modifiers, uint Key)
{
    public int ModifierCount => System.Numerics.BitOperations.PopCount(Modifiers);
}

/// <summary>Parses "Ctrl+Shift+Space" / "Ctrl+Win"-style text into a <see cref="Chord"/> and back.</summary>
public static class HotkeyParser
{
    public const string Default = "Ctrl+Shift+Space";

    public static bool TryParse(string? text, out Chord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0, virtualKey = 0;
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
        chord = new Chord(modifiers, virtualKey);
        return IsUsable(chord);
    }

    /// <summary>
    /// A key needs Ctrl, Alt or Win (a bare or Shift-only key would hijack normal typing);
    /// a modifiers-only shortcut needs at least two of them (Ctrl alone is used everywhere).
    /// </summary>
    public static bool IsUsable(Chord chord) => chord.Key != 0
        ? chord.Modifiers != 0 && chord.Modifiers != Win32.MOD_SHIFT
        : chord.ModifierCount >= 2;

    /// <summary>Parses <paramref name="text"/>, falling back to <paramref name="fallback"/> when it is invalid.</summary>
    public static (Chord chord, string text) ParseOrDefault(string? text, string fallback = Default)
    {
        if (TryParse(text, out var chord)) return (chord, Format(chord));
        Logger.Error($"[Hotkey] \"{text}\" is not a valid shortcut — using {fallback}");
        TryParse(fallback, out chord);
        return (chord, Format(chord));
    }

    public static string Format(Chord chord)
    {
        var parts = new List<string>(5);
        if ((chord.Modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((chord.Modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((chord.Modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((chord.Modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        if (chord.Key != 0) parts.Add(KeyInterop.KeyFromVirtualKey((int)chord.Key).ToString());
        return string.Join("+", parts);
    }
}

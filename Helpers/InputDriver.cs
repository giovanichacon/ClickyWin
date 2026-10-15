using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClickyWindows.Helpers;

/// <summary>
/// Real mouse and keyboard input for computer use (SendInput). Every event carries
/// <see cref="Win32.ClickyInputTag"/> and the injected flag, so Clicky's own hooks never mistake
/// it for the user's hands. Coordinates are physical screen pixels. Call from a background thread.
/// </summary>
internal static class InputDriver
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int WheelDelta = 120;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);

    public enum Button { Left, Right, Middle }

    // ── Mouse ───────────────────────────────────────────────────────────────

    public static bool MoveTo(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        int nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        int ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        return Win32.Send([Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny)]);
    }

    public static bool ButtonDown(Button b) => Win32.Send([Mouse(Flags(b).down)]);
    public static bool ButtonUp(Button b) => Win32.Send([Mouse(Flags(b).up)]);

    public static bool Click(Button b, int count, string modifiers)
    {
        var mods = ParseCombo(modifiers);
        if (!PressModifiers(mods, down: true)) return false;
        try
        {
            var (down, up) = Flags(b);
            for (int i = 0; i < count; i++)
                if (!Win32.Send([Mouse(down), Mouse(up)])) return false;
            return true;
        }
        finally { PressModifiers(mods, down: false); }
    }

    public static async Task<bool> DragAsync(int fromX, int fromY, int toX, int toY, string modifiers, CancellationToken ct)
    {
        var mods = ParseCombo(modifiers);
        if (!MoveTo(fromX, fromY) || !PressModifiers(mods, down: true)) return false;
        try
        {
            if (!ButtonDown(Button.Left)) return false;
            const int steps = 12; // intermediate moves so drag-and-drop targets notice the drag
            for (int i = 1; i <= steps; i++)
            {
                await Task.Delay(15, ct).ConfigureAwait(false);
                MoveTo(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            ButtonUp(Button.Left);
            PressModifiers(mods, down: false);
        }
    }

    public static bool Scroll(string direction, int amount, string modifiers)
    {
        var mods = ParseCombo(modifiers);
        if (!PressModifiers(mods, down: true)) return false;
        try
        {
            amount = Math.Clamp(amount, 1, 50);
            var (flag, sign) = direction.ToLowerInvariant() switch
            {
                "up" => (MOUSEEVENTF_WHEEL, 1),
                "down" => (MOUSEEVENTF_WHEEL, -1),
                "left" => (MOUSEEVENTF_HWHEEL, -1),
                "right" => (MOUSEEVENTF_HWHEEL, 1),
                _ => throw new ArgumentException($"Unknown scroll direction \"{direction}\""),
            };
            for (int i = 0; i < amount; i++)
                if (!Win32.Send([Mouse(flag, data: (uint)(sign * WheelDelta))])) return false;
            return true;
        }
        finally { PressModifiers(mods, down: false); }
    }

    private static (uint down, uint up) Flags(Button b) => b switch
    {
        Button.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        Button.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    private static Win32.INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new Win32.InputUnion
        {
            mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Win32.ClickyInputTag },
        },
    };

    // ── Keyboard ────────────────────────────────────────────────────────────

    /// <summary>Types literal text: Unicode keystrokes, with Enter and Tab as real keys.</summary>
    public static async Task<bool> TypeAsync(string text, CancellationToken ct)
    {
        var batch = new List<Win32.INPUT>();
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (c is '\n' or '\t')
            {
                ushort vk = c == '\n' ? (ushort)0x0D : (ushort)0x09;
                batch.Add(Win32.Key(vk, false));
                batch.Add(Win32.Key(vk, true));
            }
            else
            {
                batch.Add(Win32.Unicode(c, false));
                batch.Add(Win32.Unicode(c, true));
            }
            if (batch.Count >= 64)
            {
                if (!Win32.Send([.. batch])) return false;
                batch.Clear();
                await Task.Delay(8, ct).ConfigureAwait(false); // let slow apps keep up
            }
        }
        return Win32.Send([.. batch]);
    }

    /// <summary>Presses a key or combination such as "ctrl+s", "Return", "alt+F4", "super".</summary>
    public static bool Press(string combo, int repeat = 1)
    {
        var keys = ParseCombo(combo);
        if (keys.Count == 0) throw new ArgumentException($"Unknown key \"{combo}\"");
        for (int r = 0; r < Math.Clamp(repeat, 1, 100); r++)
        {
            var inputs = keys.Select(k => KeyInput(k, up: false))
                .Concat(Enumerable.Reverse(keys).Select(k => KeyInput(k, up: true)))
                .ToArray();
            if (!Win32.Send(inputs)) return false;
        }
        return true;
    }

    public static async Task<bool> HoldAsync(string combo, double seconds, CancellationToken ct)
    {
        var keys = ParseCombo(combo);
        if (keys.Count == 0) throw new ArgumentException($"Unknown key \"{combo}\"");
        if (!Win32.Send(keys.Select(k => KeyInput(k, up: false)).ToArray())) return false;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 300)), ct).ConfigureAwait(false); }
        finally { Win32.Send(Enumerable.Reverse(keys).Select(k => KeyInput(k, up: true)).ToArray()); }
        return true;
    }

    private static bool PressModifiers(List<ushort> mods, bool down) =>
        mods.Count == 0 || Win32.Send((down ? mods : Enumerable.Reverse(mods)).Select(k => KeyInput(k, up: !down)).ToArray());

    private static Win32.INPUT KeyInput(ushort vk, bool up)
    {
        var input = Win32.Key(vk, up);
        if (IsExtended(vk)) input.u.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        return input;
    }

    // Arrows, Home/End, PgUp/PgDn, Insert/Delete, right Ctrl/Alt, Win, Apps: without this flag
    // Windows treats them as the numeric keypad versions.
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D
        or 0xA3 or 0xA5 or 0x6F or 0x90;

    /// <summary>Key names as the computer-use tool sends them (xdotool style), plus common aliases.</summary>
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["option"] = 0x12,
        ["super"] = 0x5B, ["win"] = 0x5B, ["windows"] = 0x5B, ["cmd"] = 0x5B, ["meta"] = 0x5B,
        ["return"] = 0x0D, ["enter"] = 0x0D, ["kp_enter"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["tab"] = 0x09, ["backspace"] = 0x08, ["back_space"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["page_up"] = 0x21, ["pageup"] = 0x21,
        ["prior"] = 0x21, ["page_down"] = 0x22, ["pagedown"] = 0x22, ["next"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["space"] = 0x20, ["menu"] = 0x5D, ["apps"] = 0x5D, ["print"] = 0x2C, ["printscreen"] = 0x2C,
        ["caps_lock"] = 0x14, ["capslock"] = 0x14, ["num_lock"] = 0x90, ["pause"] = 0x13,
        ["minus"] = 0xBD, ["plus"] = 0xBB, ["equal"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["backslash"] = 0xDC, ["semicolon"] = 0xBA, ["apostrophe"] = 0xDE,
        ["grave"] = 0xC0, ["bracketleft"] = 0xDB, ["bracketright"] = 0xDD,
        ["audiomute"] = 0xAD, ["xf86audiomute"] = 0xAD, ["audiolowervolume"] = 0xAE, ["xf86audiolowervolume"] = 0xAE,
        ["audioraisevolume"] = 0xAF, ["xf86audioraisevolume"] = 0xAF, ["audioplay"] = 0xB3, ["xf86audioplay"] = 0xB3,
    };

    /// <summary>"ctrl+shift+t" → virtual keys in press order. Empty for "" (no modifiers).</summary>
    public static List<ushort> ParseCombo(string? combo)
    {
        var keys = new List<ushort>();
        if (string.IsNullOrWhiteSpace(combo)) return keys;
        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Named.TryGetValue(raw, out var vk)) keys.Add(vk);
            else if (raw.Length > 1 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out int f) && f is >= 1 and <= 24)
                keys.Add((ushort)(0x70 + f - 1));
            else if (raw.Length == 1)
            {
                short scan = VkKeyScan(raw[0]);
                if (scan == -1) throw new ArgumentException($"Unknown key \"{raw}\"");
                keys.Add((ushort)(scan & 0xFF));
            }
            else throw new ArgumentException($"Unknown key \"{raw}\"");
        }
        return keys;
    }
}

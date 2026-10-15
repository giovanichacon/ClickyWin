using System.Runtime.InteropServices;
using System.Threading.Channels;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>What a shortcut does.</summary>
public enum ShortcutAction { Ask, Dictate, Write, Prompt }

/// <summary>
/// Global shortcuts through a low-level keyboard hook (WH_KEYBOARD_LL) on its own thread.
///
/// - Key combos (Ctrl+Shift+Space): the key itself is swallowed so apps never see it.
/// - Modifier-only holds (Ctrl+Win): nothing is swallowed; if another key follows within
///   <see cref="ComboGrace"/> (Ctrl+Win+D, Ctrl+Win+→…) the gesture is cancelled and the key
///   goes through, so Windows shortcuts keep working.
/// - Hold to talk; a quick double-tap locks hands-free mode (tap again to finish) for bindings
///   that allow it; Esc cancels.
/// - Releasing Win or Alt after a gesture would open the Start menu / menu bar: a dummy key
///   is injected first, the same "menu mask" trick AutoHotkey uses.
/// - <see cref="BeginRecording"/> captures the next combination for the Settings window.
///
/// The hook callback only updates state and queues events, so it returns in microseconds
/// (Windows silently drops hooks that take longer than ~300 ms). Events fire, in order, on
/// one background task; subscribers marshal to the UI themselves.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    public sealed record Binding(ShortcutAction Action, Chord Chord, bool AllowHandsFree);

    private static readonly TimeSpan ComboGrace = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan TapMax = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(420);

    private const uint VK_ESCAPE = 0x1B;
    private const ushort VK_MASK = 0xE8; // unassigned: tells Windows "a key was pressed with Win/Alt"

    private enum Phase { Idle, Holding, TapPending, Locked }

    private readonly object _gate = new();
    private readonly HashSet<uint> _down = [];
    private readonly HashSet<uint> _swallowUp = [];
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Win32.LowLevelKeyboardProc _proc; // kept alive: the hook holds a pointer to it
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    private IReadOnlyList<Binding> _bindings = [];
    private Phase _phase;
    private Binding? _active;
    private DateTime _startedAt;
    private int _gestureId;
    private bool _maskWinAlt;

    private Action<string?>? _recordDone;

    // Seams for tests: physical key state and synthetic input
    internal Func<uint, bool> IsPhysicallyDown = vk => Win32.IsKeyDown((int)vk);
    internal Func<Win32.INPUT[], bool> Inject = Win32.Send;
    private uint _recModifiers, _recKey;

    /// <summary>A gesture began (key pressed) — start listening.</summary>
    public event Action<ShortcutAction>? Started;
    /// <summary>The gesture ended normally (key released, or tapped again in hands-free mode).</summary>
    public event Action<ShortcutAction>? Finished;
    /// <summary>Esc, a Windows shortcut that shares the modifiers, or a lone short tap.</summary>
    public event Action<ShortcutAction>? Cancelled;
    /// <summary>Double-tap: keeps listening after the keys are released.</summary>
    public event Action<ShortcutAction>? HandsFreeLocked;

    public KeyboardHookService()
    {
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Clicky keyboard hook" };
        _ = Task.Run(DispatchEventsAsync);
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Drops a locked hands-free gesture without events (the session ended by voice or silence).</summary>
    public void ReleaseHandsFree(ShortcutAction action)
    {
        lock (_gate)
        {
            if (_phase != Phase.Locked || _active?.Action != action) return;
            _active = null;
            _phase = Phase.Idle;
            _gestureId++;
        }
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread.Start(ready);
        ready.Wait(TimeSpan.FromSeconds(2));
        if (_hook == IntPtr.Zero) Logger.Error("[Keys] Could not install the keyboard hook — shortcuts won't work");
    }

    public void SetBindings(IEnumerable<Binding> bindings)
    {
        lock (_gate)
        {
            _bindings = bindings.Where(b => HotkeyParser.IsUsable(b.Chord)).ToList();
            Logger.Log("[Keys] Shortcuts: " + string.Join(", ", _bindings.Select(b => $"{b.Action}={HotkeyParser.Format(b.Chord)}")));
        }
    }

    /// <summary>Captures the next combination (all keys swallowed); calls back with its text, or null on Esc.</summary>
    public void BeginRecording(Action<string?> onDone)
    {
        lock (_gate)
        {
            _recordDone = onDone;
            _recModifiers = _recKey = 0;
            if (_phase != Phase.Idle && _active != null) CancelActive();
        }
    }

    public void CancelRecording()
    {
        lock (_gate) _recordDone = null;
    }

    // ── Hook thread ─────────────────────────────────────────────────────────

    private void Run(object? state)
    {
        _threadId = Win32.GetCurrentThreadId();
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Logger.Log($"[Keys] SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
        ((ManualResetEventSlim)state!).Set();

        // Low-level hooks are called through this thread's message loop
        while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            // Our own typing/paste and other apps' synthetic keys are not the user's hands
            if ((info.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                bool swallow;
                try
                {
                    lock (_gate) swallow = down ? OnKeyDown(info.vkCode) : OnKeyUp(info.vkCode);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Keys] Hook error: {ex.Message}");
                    swallow = false;
                }
                if (swallow) return 1;
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Feeds one physical key event, as the hook does (tests).</summary>
    internal bool Feed(uint vk, bool down)
    {
        lock (_gate) return down ? OnKeyDown(vk) : OnKeyUp(vk);
    }

    // ── Key state (called under _gate) ──────────────────────────────────────

    private static uint ModifierOf(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Win32.MOD_SHIFT,
        0x11 or 0xA2 or 0xA3 => Win32.MOD_CONTROL,
        0x12 or 0xA4 or 0xA5 => Win32.MOD_ALT,
        0x5B or 0x5C => Win32.MOD_WIN,
        _ => 0,
    };

    private uint HeldModifiers()
    {
        uint mods = 0;
        foreach (var vk in _down) mods |= ModifierOf(vk);
        return mods;
    }

    private bool OtherKeyHeld(uint except)
    {
        foreach (var vk in _down)
            if (vk != except && ModifierOf(vk) == 0) return true;
        return false;
    }

    /// <summary>
    /// Drops keys whose key-up we never saw (lock screen, UAC prompt, focus to an admin window).
    /// Keys we swallowed are kept: Windows never saw them go down, so it reports them as up.
    /// </summary>
    private void PruneStale(uint current)
    {
        _down.RemoveWhere(vk => vk != current && !_swallowUp.Contains(vk) && !IsPhysicallyDown(vk));
    }

    private Binding? Match(uint vk)
    {
        uint mods = HeldModifiers();
        foreach (var b in _bindings)
        {
            if (b.Chord.Modifiers != mods) continue;
            if (b.Chord.Key != 0 ? b.Chord.Key == vk : ModifierOf(vk) != 0 && !OtherKeyHeld(0))
                return b;
        }
        return null;
    }

    private bool OnKeyDown(uint vk)
    {
        bool repeat = !_down.Add(vk);
        if (!repeat && _recordDone == null) PruneStale(vk);

        if (_recordDone != null) return RecordDown(vk);
        if (repeat) return _swallowUp.Contains(vk); // auto-repeat of a swallowed key stays swallowed

        if (vk == VK_ESCAPE && _phase != Phase.Idle && _active != null)
        {
            CancelActive();
            return Swallow(vk);
        }

        switch (_phase)
        {
            case Phase.Idle:
                return TryStart(vk);

            case Phase.Holding:
                if (vk == _active!.Chord.Key) return Swallow(vk);
                // Another key soon after a modifier-only start: it was a Windows shortcut, not us
                if (DateTime.UtcNow - _startedAt < ComboGrace)
                {
                    CancelActive();
                    return TryStart(vk);
                }
                return false; // long hold: keep listening, let the key through

            case Phase.TapPending:
                if (Match(vk) is { } again && again.Action == _active!.Action)
                {
                    _phase = Phase.Locked;
                    var action = again.Action;
                    Queue(() => HandsFreeLocked?.Invoke(action));
                    return again.Chord.Key != 0 && Swallow(vk);
                }
                if (PartOf(_active!.Chord, vk)) return false; // second tap still being pressed
                CancelActive();
                return TryStart(vk);

            case Phase.Locked:
                if (Match(vk) is { } stop && stop.Action == _active!.Action)
                {
                    FinishActive();
                    return stop.Chord.Key != 0 && Swallow(vk);
                }
                return false;
        }
        return false;
    }

    private bool OnKeyUp(uint vk)
    {
        _down.Remove(vk);

        if (_recordDone != null) return RecordUp(vk);

        bool swallow = _swallowUp.Remove(vk);
        uint mod = ModifierOf(vk);

        if (_phase == Phase.Holding && _active != null && PartOf(_active.Chord, vk))
        {
            if (_active.AllowHandsFree && DateTime.UtcNow - _startedAt < TapMax)
            {
                // Maybe the first half of a double-tap: decide when the window closes
                _phase = Phase.TapPending;
                int id = _gestureId;
                _ = Task.Delay(DoubleTapWindow).ContinueWith(_ =>
                {
                    lock (_gate)
                        if (_gestureId == id && _phase == Phase.TapPending) CancelActive();
                });
            }
            else
            {
                FinishActive();
            }
        }

        // Win/Alt released after being part of a gesture: mask it so no Start menu / menu bar opens
        if (_maskWinAlt && mod is Win32.MOD_WIN or Win32.MOD_ALT)
        {
            if ((HeldModifiers() & (Win32.MOD_WIN | Win32.MOD_ALT)) == 0) _maskWinAlt = false;
            Inject([Win32.Key(VK_MASK, false), Win32.Key(VK_MASK, true), Win32.Key((ushort)vk, true)]);
            return true; // the injected copy of this key-up replaces the real one
        }
        return swallow;
    }

    private static bool PartOf(Chord chord, uint vk) => vk == chord.Key || (chord.Modifiers & ModifierOf(vk)) != 0;

    private bool TryStart(uint vk)
    {
        var binding = Match(vk);
        if (binding == null) return false;

        _active = binding;
        _phase = Phase.Holding;
        _startedAt = DateTime.UtcNow;
        _gestureId++;
        if ((binding.Chord.Modifiers & (Win32.MOD_WIN | Win32.MOD_ALT)) != 0) _maskWinAlt = true;

        var action = binding.Action;
        Queue(() => Started?.Invoke(action));
        return binding.Chord.Key != 0 && Swallow(vk);
    }

    private void FinishActive()
    {
        var action = _active!.Action;
        _active = null;
        _phase = Phase.Idle;
        _gestureId++;
        Queue(() => Finished?.Invoke(action));
    }

    private void CancelActive()
    {
        var action = _active!.Action;
        _active = null;
        _phase = Phase.Idle;
        _gestureId++;
        Queue(() => Cancelled?.Invoke(action));
    }

    private bool Swallow(uint vk)
    {
        _swallowUp.Add(vk);
        return true;
    }

    // ── Recording a new combination (Settings) ──────────────────────────────

    private bool RecordDown(uint vk)
    {
        if (vk == VK_ESCAPE && _recModifiers == 0)
        {
            CompleteRecording(null);
            return Swallow(vk);
        }
        uint mod = ModifierOf(vk);
        if (mod != 0) _recModifiers |= mod;
        else if (_recKey == 0) _recKey = vk;
        return Swallow(vk); // swallowing Win too means no Start menu while recording
    }

    private bool RecordUp(uint vk)
    {
        _swallowUp.Remove(vk);
        if (_down.Count == 0 && (_recModifiers != 0 || _recKey != 0))
            CompleteRecording(HotkeyParser.Format(new Chord(_recModifiers, _recKey)));
        return true;
    }

    private void CompleteRecording(string? text)
    {
        var done = _recordDone;
        _recordDone = null;
        _recModifiers = _recKey = 0;
        if (done != null) Queue(() => done(text));
    }

    // ── Events ──────────────────────────────────────────────────────────────

    private void Queue(Action action) => _events.Writer.TryWrite(action);

    private async Task DispatchEventsAsync()
    {
        await foreach (var action in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { action(); }
            catch (Exception ex) { Logger.Error($"[Keys] Shortcut handler failed: {ex}"); }
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _events.Writer.TryComplete();
    }
}

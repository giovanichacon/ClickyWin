using System.Runtime.InteropServices;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// While Clicky operates the computer, watches for the user's own hands: any physical key
/// (Esc included) or a real mouse move/click/scroll fires <see cref="Interrupted"/> once.
/// Synthetic input (Clicky's own SendInput, flagged as injected) is ignored. Low-level hooks
/// run on their own thread with a message loop; nothing is installed while idle.
/// </summary>
public sealed class UserInputWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x01;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int MoveThreshold = 12; // physical px: ignore a nudged desk

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Win32.POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private Win32.LowLevelKeyboardProc? _keyProc, _mouseProc; // kept alive while hooked
    private volatile bool _armed;
    private Win32.POINT _origin;
    private bool _haveOrigin;

    /// <summary>Fires on a hook thread with a short reason ("Esc", "key", "mouse").</summary>
    public event Action<string>? Interrupted;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(Run) { IsBackground = true, Name = "Clicky input watcher" };
            _thread.Start(ready);
            ready.Wait(2000);
        }
    }

    /// <summary>Arms or disarms without unhooking (paused while Clicky asks the user something).</summary>
    public bool Armed
    {
        get => _armed;
        set
        {
            _haveOrigin = Win32.GetCursorPos(out _origin);
            _armed = value;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _armed = false;
            if (_thread == null) return;
            Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }
    }

    private void Run(object? state)
    {
        _threadId = Win32.GetCurrentThreadId();
        _keyProc = KeyProc;
        _mouseProc = MouseProc;
        var module = Win32.GetModuleHandle(null);
        var keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, module, 0);
        var mouseHook = Win32.SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (keyHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            Logger.Log($"[Agent] Input watcher hook failed (error {Marshal.GetLastWin32Error()})");
        ((ManualResetEventSlim)state!).Set();

        while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        if (keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(keyHook);
        if (mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(mouseHook);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _armed)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            if ((info.flags & Win32.LLKHF_INJECTED) == 0 && msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                bool esc = info.vkCode == 0x1B;
                Fire(esc ? "Esc" : "key");
                if (esc) return 1; // Esc stops Clicky; don't also send it to the app
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _armed)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) != 0)
            {
                if ((int)wParam == WM_MOUSEMOVE) (_origin, _haveOrigin) = (info.pt, true); // Clicky moved it
            }
            else
            {
                if ((int)wParam != WM_MOUSEMOVE)
                    Fire("mouse");
                else if (!_haveOrigin)
                {
                    _origin = info.pt;
                    _haveOrigin = true;
                }
                else if (Math.Abs(info.pt.X - _origin.X) > MoveThreshold || Math.Abs(info.pt.Y - _origin.Y) > MoveThreshold)
                    Fire("mouse");
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Fire(string reason)
    {
        if (!_armed) return;
        _armed = false;
        var handler = Interrupted;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler(reason)); // never block the hook
    }

    public void Dispose() => Stop();
}

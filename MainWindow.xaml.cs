using System.Windows;
using ClickyWindows.Helpers;
using ClickyWindows.Services;

namespace ClickyWindows;

/// <summary>
/// Invisible message-pump window. Hosts the HotkeyService (requires an HWND)
/// and drives CompanionManager.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey;
    private readonly CompanionManager _companion;
    private readonly OverlayWindow _overlay;

    public MainWindow(uint hotkeyModifiers, uint hotkeyVirtualKey, CompanionManager companion, OverlayWindow overlay)
    {
        InitializeComponent();

        _companion = companion;
        _overlay = overlay;
        _hotkey = new HotkeyService(hotkeyModifiers, hotkeyVirtualKey);

        // Companion events fire on background threads. Marshal with InvokeAsync, never Invoke:
        // a blocking hop would stall the caller (audio thread, network loops) whenever the UI is busy.
        _companion.StateChanged += state =>
            Dispatcher.InvokeAsync(() => _overlay.SetState(state));

        _companion.PointReceived += (x, y, label) =>
            Dispatcher.InvokeAsync(() => _overlay.ShowTargetAt(x, y, label));

        // ~100 updates/s from the audio thread: just store the value, the render tick reads it
        _companion.AudioLevelChanged += _overlay.SetAudioLevel;

        _companion.FeedbackReceived += msg =>
            Dispatcher.InvokeAsync(() => _overlay.ShowFeedback(msg));

        _companion.TranscriptConfirmed += () =>
            Dispatcher.InvokeAsync(_overlay.PulseSpinner);

        _hotkey.PushToTalkPressed  += _companion.OnPushToTalkPressed;
        _hotkey.PushToTalkReleased += _companion.OnPushToTalkReleased;

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    public bool TryChangeHotkey(uint modifiers, uint virtualKey) => _hotkey.TryChange(modifiers, virtualKey);

    public void SuspendHotkey(bool suspended) => _hotkey.Suspend(suspended);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hotkey.Register(this);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Error($"Push-to-talk hotkey unavailable: {ex.Message}");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hotkey.Dispose();
    }
}

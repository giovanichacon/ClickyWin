using System.Windows;
using ClickyWindows.Services;

namespace ClickyWindows;

/// <summary>
/// Invisible message-pump window (keeps the WPF app alive). Forwards companion and dictation
/// events to the overlay.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(CompanionManager companion, DictationManager dictation, OverlayWindow overlay)
    {
        InitializeComponent();

        // Events fire on background threads. Marshal with InvokeAsync, never Invoke:
        // a blocking hop would stall the caller (audio thread, network loops) whenever the UI is busy.
        companion.StateChanged += state => Dispatcher.InvokeAsync(() => overlay.SetState(state));
        companion.PointReceived += (x, y, label) => Dispatcher.InvokeAsync(() => overlay.ShowTargetAt(x, y, label));
        companion.FeedbackReceived += msg => Dispatcher.InvokeAsync(() => overlay.ShowFeedback(msg));
        companion.TranscriptConfirmed += () => Dispatcher.InvokeAsync(overlay.PulseSpinner);
        // ~100 updates/s from the audio thread: just store the value, the render tick reads it
        companion.AudioLevelChanged += overlay.SetAudioLevel;

        dictation.StateChanged += state => Dispatcher.InvokeAsync(() => overlay.SetState(state));
        dictation.FeedbackReceived += msg => Dispatcher.InvokeAsync(() => overlay.ShowFeedback(msg));
        dictation.AudioLevelChanged += overlay.SetAudioLevel;
    }
}

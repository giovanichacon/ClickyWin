using System.Globalization;
using System.Speech.Synthesis;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Free, offline text-to-speech with the voices installed in Windows (SAPI): picks a
/// Spanish voice for Spanish replies and an English one otherwise. Used when ElevenLabs
/// is not configured or fails (no key, quota used up, offline).
/// </summary>
public sealed class WindowsVoiceService : IDisposable
{
    private readonly object _lock = new();
    private SpeechSynthesizer? _synth;
    private Prompt? _current;

    public async Task SpeakAsync(string text, Action? onPlaybackStarting, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Prompt? prompt = null;

        lock (_lock)
        {
            var synth = Synth();
            SelectVoice(synth, L.LooksSpanish(text) ? "es" : "en");

            void OnCompleted(object? s, SpeakCompletedEventArgs e)
            {
                // Taking the lock guarantees `prompt` is assigned; a cancelled earlier prompt is ignored
                lock (_lock)
                    if (e.Prompt != prompt) return;
                synth.SpeakCompleted -= OnCompleted;
                if (e.Cancelled) done.TrySetCanceled(ct);
                else if (e.Error != null) done.TrySetException(e.Error);
                else done.TrySetResult();
            }
            synth.SpeakCompleted += OnCompleted;

            onPlaybackStarting?.Invoke();
            prompt = synth.SpeakAsync(text);
            _current = prompt;
        }

        using var reg = ct.Register(Stop);
        await done.Task.ConfigureAwait(false);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_synth != null && _current != null)
                _synth.SpeakAsyncCancel(_current);
        }
    }

    private SpeechSynthesizer Synth()
    {
        if (_synth != null) return _synth;

        // Created without a SynchronizationContext so SpeakCompleted is not posted to the UI thread
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { _synth = new SpeechSynthesizer(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate = 1; // a little brisker than default
        Logger.Log("[TTS] Windows voices: " +
                   string.Join(", ", _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)));
        return _synth;
    }

    private static void SelectVoice(SpeechSynthesizer synth, string language)
    {
        var voice = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == language);
        if (voice != null && synth.Voice.Name != voice.Name)
            synth.SelectVoice(voice.Name);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _synth?.SpeakAsyncCancelAll();
            _synth?.Dispose();
            _synth = null;
        }
    }
}

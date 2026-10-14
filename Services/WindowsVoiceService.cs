using System.Globalization;
using System.Speech.Synthesis;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Free, offline text-to-speech with the voices installed in Windows (SAPI): picks a
/// Spanish voice for Spanish replies and an English one otherwise. Used when ElevenLabs
/// is not configured or fails (no key, quota used up, offline).
/// </summary>
public sealed class WindowsVoiceService(Func<string>? preferredVoice = null, Func<double>? speed = null) : IDisposable
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
            SelectVoice(synth, preferredVoice?.Invoke(), L.LooksSpanish(text) ? "es" : "en");
            synth.Rate = RateFor(speed?.Invoke() ?? 1.0);

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
        Logger.Log("[TTS] Windows voices: " +
                   string.Join(", ", _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)));
        return _synth;
    }

    /// <summary>Installed, enabled Windows voices as (name, language tag).</summary>
    /// <summary>
    /// SAPI rate runs -10..10, where +10 is about 3× faster. Speed 1.0 maps to +1 (a little
    /// brisker than SAPI's default, which sounds slow next to ElevenLabs).
    /// </summary>
    private static int RateFor(double speed) =>
        Math.Clamp((int)Math.Round(1 + 10 * Math.Log(Math.Clamp(speed, 0.5, 2.0)) / Math.Log(3)), -10, 10);

    public static List<(string name, string culture)> InstalledVoices()
    {
        using var synth = new SpeechSynthesizer();
        return synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => (v.VoiceInfo.Name, v.VoiceInfo.Culture.Name))
            .ToList();
    }

    private static void SelectVoice(SpeechSynthesizer synth, string? preferred, string language)
    {
        var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        var voice = voices.FirstOrDefault(v => !string.IsNullOrEmpty(preferred) && v.Name == preferred)
                    ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == language);
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

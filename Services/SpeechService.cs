using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Speaks replies with ElevenLabs when configured, otherwise (or when ElevenLabs fails
/// before any audio played — no key, quota used up, offline) with the free Windows voice.
/// </summary>
public sealed class SpeechService(AppSettings settings) : IDisposable
{
    private readonly ElevenLabsService _elevenLabs = new(settings);
    private readonly WindowsVoiceService _windows = new(() => settings.WindowsVoice);
    private int _elevenLabsErrorShown;

    public async Task SpeakAsync(string text, Action? onPlaybackStarting, CancellationToken ct)
    {
        if (settings.UseElevenLabs && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey))
        {
            bool started = false;
            try
            {
                await _elevenLabs.SpeakAsync(text, () => { started = true; onPlaybackStarting?.Invoke(); }, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !started)
            {
                var message = $"[TTS] ElevenLabs failed, using the Windows voice: {ex.Message}";
                // Balloon the first time only; afterwards just log
                if (Interlocked.Exchange(ref _elevenLabsErrorShown, 1) == 0) Logger.Error(message);
                else Logger.Log(message);
            }
        }
        await _windows.SpeakAsync(text, onPlaybackStarting, ct).ConfigureAwait(false);
    }

    public void StopPlayback()
    {
        _elevenLabs.StopPlayback();
        _windows.Stop();
    }

    public void Dispose()
    {
        _elevenLabs.Dispose();
        _windows.Dispose();
    }
}

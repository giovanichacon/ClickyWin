using System.Text;
using System.Threading.Channels;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Speaks replies sentence by sentence as they arrive: ElevenLabs when configured, otherwise
/// (or from the first sentence ElevenLabs fails on — no key, quota used up, offline) the free
/// Windows voice.
/// </summary>
public sealed class SpeechService(AppSettings settings) : IDisposable
{
    private readonly ElevenLabsService _elevenLabs = new(settings);
    private readonly WindowsVoiceService _windows = new(() => settings.WindowsVoice, () => settings.EffectiveVoiceSpeed);
    private int _elevenLabsErrorShown;

    public Task SpeakAsync(string text, Action? onPlaybackStarting, CancellationToken ct)
    {
        var single = Channel.CreateUnbounded<string>();
        single.Writer.TryWrite(text);
        single.Writer.Complete();
        return SpeakSentencesAsync(single.Reader, onPlaybackStarting, ct);
    }

    /// <summary>Speaks sentences in order until the channel completes. <paramref name="onPlaybackStarting"/> fires once.</summary>
    public async Task SpeakSentencesAsync(ChannelReader<string> sentences, Action? onPlaybackStarting, CancellationToken ct)
    {
        bool started = false;
        void OnStart()
        {
            if (started) return;
            started = true;
            onPlaybackStarting?.Invoke();
        }

        bool useElevenLabs = settings.UseElevenLabs && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey);
        using var playback = useElevenLabs ? _elevenLabs.BeginPlayback(OnStart) : null;
        var spoken = new StringBuilder();

        await foreach (var sentence in sentences.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            if (playback != null && useElevenLabs)
            {
                try
                {
                    await playback.AppendAsync(sentence, spoken.ToString(), ct).ConfigureAwait(false);
                    spoken.Append(sentence).Append(' ');
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    useElevenLabs = false;
                    var message = $"[TTS] ElevenLabs failed, using the Windows voice: {ex.Message}";
                    // Balloon the first time only; afterwards just log
                    if (Interlocked.Exchange(ref _elevenLabsErrorShown, 1) == 0) Logger.Error(message);
                    else Logger.Log(message);
                    await playback.DrainAsync(ct).ConfigureAwait(false); // finish what already played
                }
            }

            await _windows.SpeakAsync(sentence, OnStart, ct).ConfigureAwait(false);
            spoken.Append(sentence).Append(' ');
        }

        if (playback != null && useElevenLabs)
            await playback.DrainAsync(ct).ConfigureAwait(false);
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

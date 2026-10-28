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

    /// <summary>The reply's words heard so far, revealed as the voice says them (captions).</summary>
    public event Action<string>? Caption;

    private static int _talking;
    private static long _lastTalkEndedTicks;
    private static volatile bool _elevenLabsOutOfCredits; // shortcut: lasts until restart; credits reset monthly

    /// <summary>True while any Clicky voice is playing (the hands-free session mutes the mic then).</summary>
    public static bool IsTalking => Volatile.Read(ref _talking) > 0;

    /// <summary>When Clicky's voice last stopped (UTC).</summary>
    public static DateTime LastTalkEnded => new(Interlocked.Read(ref _lastTalkEndedTicks), DateTimeKind.Utc);

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
            Interlocked.Increment(ref _talking);
            onPlaybackStarting?.Invoke();
        }
        try
        {
            await SpeakCoreAsync(sentences, OnStart, ct).ConfigureAwait(false);
        }
        finally
        {
            if (started)
            {
                Interlocked.Exchange(ref _lastTalkEndedTicks, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref _talking);
            }
        }
    }

    private async Task SpeakCoreAsync(ChannelReader<string> sentences, Action OnStart, CancellationToken ct)
    {
        bool useElevenLabs = settings.UseElevenLabs && !string.IsNullOrWhiteSpace(settings.ElevenLabsApiKey) && !_elevenLabsOutOfCredits;
        void ShowCaption(string heard)
        {
            if (!ct.IsCancellationRequested) Caption?.Invoke(CaptionTail(heard));
        }
        using var playback = useElevenLabs ? _elevenLabs.BeginPlayback(OnStart, ShowCaption) : null;
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
                    // Out of monthly credits: every later sentence would fail the same way, so stop asking until restart
                    if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) _elevenLabsOutOfCredits = true;
                    var message = $"[TTS] ElevenLabs failed, using the Windows voice: {ex.Message}";
                    // Balloon the first time only; afterwards just log
                    if (Interlocked.Exchange(ref _elevenLabsErrorShown, 1) == 0) Logger.Error(message);
                    else Logger.Log(message);
                    await playback.DrainAsync(ct).ConfigureAwait(false); // finish what already played
                }
            }

            var before = spoken.ToString();
            await _windows.SpeakAsync(sentence, OnStart, ct,
                end => ShowCaption(before + ElevenLabsService.Playback.StartedWords(sentence, (double)end / sentence.Length))).ConfigureAwait(false);
            spoken.Append(sentence).Append(' ');
        }

        if (playback != null && useElevenLabs)
            await playback.DrainAsync(ct).ConfigureAwait(false);
    }

    private const int CaptionChars = 72; // ~2 lines in the 220 px caption box

    /// <summary>The last ~2 lines of the heard text, cut at a word.</summary>
    internal static string CaptionTail(string heard)
    {
        heard = heard.Trim();
        if (heard.Length <= CaptionChars) return heard;
        int cut = heard.IndexOf(' ', heard.Length - CaptionChars);
        return "…" + (cut < 0 ? heard[^CaptionChars..] : heard[(cut + 1)..]);
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

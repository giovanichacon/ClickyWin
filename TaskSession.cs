using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Hands-free task session (double-tap the Ask shortcut): the mic stays open and every finished
/// sentence is a command, with no key to hold. Tasks run without the Allow card (starting the
/// session was the approval); questions before irreversible steps are said out loud and answered
/// by voice or click. While a task runs, only "stop" is listened for. What Clicky hears while it
/// is speaking is ignored, so it doesn't answer itself. Ends on "end session", Esc, a double-tap,
/// or several minutes of silence.
/// </summary>
public sealed class TaskSession(AppSettings settings, AudioCaptureService mic, CompanionManager companion,
    AgentController agent, AgentCard card)
{
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(500); // room echo after Clicky's voice stops

    private readonly object _gate = new();
    private VoiceSession? _voice;
    private CancellationTokenSource? _cts;
    private DateTime _lastActivity;

    public bool IsActive { get; private set; }

    /// <summary>The session ended by itself (voice, silence, mic or connection failure); the hotkey gesture must be released.</summary>
    public event Action? EndedByItself;

    /// <param name="withMic">False for tests: turns come only from <see cref="Hear"/>.</param>
    public void Start(bool withMic = true)
    {
        lock (_gate)
        {
            if (IsActive) return;
            IsActive = true;
            _lastActivity = DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            if (withMic)
            {
                _voice = new VoiceSession();
                // Clicky's own voice must never reach speech-to-text (speakers, no headphones): mute the mic while it talks
                _voice.Start(settings, mic, onLevel: null, onTurn: text => Task.Run(() => Hear(text)), muted: ClickyTalking);
            }
        }
        Logger.Log("[Session] Started");
        agent.InSession = true;
        card.SetIdleBanner(L.SessionTitle, L.SessionHint);
        _ = companion.SayAsync(L.SessionStarted);
        _ = WatchAsync(_cts.Token);
    }

    public void End(string reason)
    {
        VoiceSession? voice;
        lock (_gate)
        {
            if (!IsActive) return;
            IsActive = false;
            voice = _voice;
            _voice = null;
            _cts?.Cancel();
        }
        Logger.Log($"[Session] Ended ({reason})");
        agent.InSession = false;
        card.SetIdleBanner(null, null);
        if (voice != null) _ = voice.DisposeAsync().AsTask();
        _ = companion.SayAsync(L.SessionEnded);
    }

    private static bool ClickyTalking() => SpeechService.IsTalking || DateTime.UtcNow - SpeechService.LastTalkEnded < EchoTail;

    /// <summary>One finished sentence from the user (from the mic, or a test).</summary>
    public void Hear(string text)
    {
        if (!IsActive) return;
        var now = DateTime.UtcNow;
        lock (_gate) _lastActivity = now;

        // Answering Clicky's question (Yes/No or a choice) works even while its voice is still asking
        if (card.TryAnswer(text))
        {
            Logger.Log($"[Session] Answer: \"{text}\"");
            return;
        }
        if (agent.IsRunning)
        {
            if (IsStop(text))
            {
                Logger.Log($"[Session] Stop: \"{text}\"");
                agent.Stop();
            }
            else Logger.Log($"[Session] Ignored while working: \"{text}\"");
            return;
        }
        if (IsEnd(text))
        {
            End("voice");
            EndedByItself?.Invoke();
            return;
        }
        if (IsStop(text)) // nothing running: "stop" just silences Clicky
        {
            companion.StopSpeaking();
            return;
        }

        Logger.Log($"[Session] Heard: \"{text}\"");
        L.NoteSpoken(text);
        companion.AskText(text);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (agent.IsRunning || companion.State != AppState.Idle)
                {
                    lock (_gate) _lastActivity = DateTime.UtcNow; // working or talking is not silence
                    continue;
                }
                bool silent;
                lock (_gate) silent = DateTime.UtcNow - _lastActivity > SilenceLimit;
                var asr = _voice?.Asr;
                if (silent || asr?.Error != null)
                {
                    End(silent ? "silence" : $"speech-to-text failed: {asr!.Error}");
                    EndedByItself?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Voice commands ──────────────────────────────────────────────────────

    private static readonly string[] StopWords = ["stop", "stop it", "para", "páralo", "paralo", "detente", "alto", "cancel", "cancela", "wait", "espera"];
    private static readonly string[] EndPhrases =
    [
        "end session", "end the session", "stop the session", "stop session", "that's all", "that's it for now",
        "terminar sesión", "termina la sesión", "terminar la sesión", "fin de la sesión", "eso es todo",
    ];

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '\'' or ' ' ? c : ' ').ToArray()).Trim();

    /// <summary>The last sentence of a turn: "…and that's it. Stop." → "Stop." (a command often follows other words).</summary>
    private static string LastSentence(string text) =>
        text.Split(['.', '!', '?', '¡', '¿'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? text;

    /// <summary>A short command made of stop words ("Stop.", "Para, para"), alone or as the last sentence.</summary>
    private static bool IsStop(string text) => IsStopWords(text) || IsStopWords(LastSentence(text));

    private static bool IsStopWords(string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 4 && words.Any(w => StopWords.Contains(w)) &&
               words.All(w => StopWords.Contains(w) || w is "please" or "por" or "favor" or "clicky" or "it" or "now" or "ya");
    }

    /// <summary>"End session" alone, or in the last sentence or last few words of a longer turn.</summary>
    private static bool IsEnd(string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join(' ', words.TakeLast(6));
        var last = Normalize(LastSentence(text));
        return EndPhrases.Any(p => tail.Contains(p) || (last.Split(' ').Length <= 8 && last.Contains(p)));
    }
}

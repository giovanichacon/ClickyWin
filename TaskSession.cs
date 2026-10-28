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
/// is speaking is ignored (except "stop"), so it doesn't answer itself. Ends on "end session", Esc, a double-tap,
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
                // The mic stays live while Clicky talks so "stop" is heard; Hear drops the echo of its own voice
                _voice.Start(settings, mic, onLevel: null, onTurn: (text, started) => Task.Run(() => Hear(text, started)));
            }
        }
        Logger.Log("[Session] Started");
        if (settings.SessionDucking) AudioDucker.Duck();
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
        agent.Stop(); // ending the session also stops a task in progress
        companion.Teacher.Stop("session ended");
        agent.InSession = false;
        card.SetIdleBanner(null, null);
        if (voice != null) _ = voice.DisposeAsync().AsTask();
        AudioDucker.Restore();
        _ = companion.SayAsync(L.SessionEnded);
    }

    /// <summary>Clicky's voice played while this turn was spoken, so it may hold its echo.</summary>
    private static bool TalkedDuringTurn(DateTime started) =>
        SpeechService.IsTalking || SpeechService.LastTalkEnded + EchoTail > started;

    /// <summary>One finished sentence from the user (from the mic, or a test), and when its first words arrived.</summary>
    public void Hear(string text, DateTime? started = null)
    {
        if (!IsActive) return;
        var now = DateTime.UtcNow;
        lock (_gate) _lastActivity = now;

        // A running lesson takes its commands first; over Clicky's voice only a whole-turn command counts
        if (!agent.IsRunning && companion.Teacher.TryCommand(text, exact: started is { } t && TalkedDuringTurn(t)))
            return;

        // Over Clicky's voice (speakers, no headphones) only "stop" counts; the rest is its own echo
        if (started is { } s && TalkedDuringTurn(s)) // typed test lines (no start time) are never echo
        {
            if (IsStop(text) || EndsWithStop(text))
            {
                Logger.Log($"[Session] Stop over voice: \"{text}\"");
                companion.StopSpeaking();
                if (agent.IsRunning) agent.Stop();
                return;
            }
            // The user often talks over the end of Clicky's reply, so the turn holds both: keep their part
            if (StripEcho(text, SpeechService.RecentSpoken) is not { } own)
            {
                Logger.Log($"[Session] Echo: \"{text}\"");
                return;
            }
            Logger.Log($"[Session] Echo stripped: \"{text}\" → \"{own}\"");
            text = own;
        }
        if (agent.IsRunning && IsStop(text)) // before TryAnswer: a free-form answer would swallow "Stop."
        {
            Logger.Log($"[Session] Stop: \"{text}\"");
            agent.Stop();
            return;
        }

        if (card.TryAnswer(text))
        {
            Logger.Log($"[Session] Answer: \"{text}\"");
            return;
        }
        if (IsNoise(text))
        {
            Logger.Log($"[Session] Noise: \"{text}\"");
            return;
        }
        if (agent.IsRunning)
        {
            _ = OnHeardWhileWorkingAsync(text);
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

        // A bare "no" / "yeah" is only an answer when Clicky just asked something; otherwise it's music or chatter
        if (IsBareReply(text) && !companion.LastReply.TrimEnd().EndsWith('?'))
        {
            Logger.Log($"[Session] Not a request: \"{text}\"");
            return;
        }

        Logger.Log($"[Session] Heard: \"{text}\"");
        L.NoteSpoken(text);
        companion.AskText(text, session: true);
    }

    /// <summary>
    /// Words heard while a task runs: a correction ("no, Claude Code, not Cloudflare") stops the task and
    /// becomes the next request; "stop" in other words stops it; lyrics, TV and chatter are ignored.
    /// </summary>
    private async Task OnHeardWhileWorkingAsync(string text)
    {
        var task = agent.CurrentTask;
        var verdict = await companion.ClassifyDuringTaskAsync(task, text, _cts?.Token ?? CancellationToken.None)
            .ConfigureAwait(false);
        Logger.Log($"[Session] While working ({verdict}): \"{text}\"");
        if (verdict == "IGNORE" || !IsActive) return;

        agent.Stop();
        if (verdict != "CHANGE") return;
        for (int i = 0; i < 50 && agent.IsRunning; i++) await Task.Delay(100).ConfigureAwait(false);
        L.NoteSpoken(text);
        companion.AskText($"(While Clicky was doing \"{task}\", the user said:) {text}", session: true);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (settings.SessionDucking) AudioDucker.Duck(); // apps that started playing since (a task opened YouTube)
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

    private static readonly string[] Fillers = ["mm", "mmm", "hmm", "hm", "uh", "um", "uhm", "ah", "oh", "eh", "huh", "la", "na", "ooh", "yeah yeah"];
    private static readonly string[] BareReplies =
        ["no", "nope", "nah", "yes", "yeah", "yep", "yup", "ok", "okay", "sure", "right", "sí", "si", "vale", "claro", "bueno"];

    /// <summary>
    /// Music and hums: no Latin-script letters (speech-to-text writes a hum as "嗯"), or only filler sounds.
    /// The user speaks English or Spanish, so anything else isn't for Clicky.
    /// </summary>
    private static bool IsNoise(string text)
    {
        if (!text.Any(c => char.IsLetter(c) && c < 0x250)) return true;
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => Fillers.Contains(w) || w.Length > 1 && w.Distinct().Count() == 1);
    }

    /// <summary>One or two words that only make sense as an answer ("Nope.", "Yeah, okay").</summary>
    private static bool IsBareReply(string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 2 && words.All(BareReplies.Contains);
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '\'' or ' ' ? c : ' ').ToArray()).Trim();

    /// <summary>The last sentence of a turn: "…and that's it. Stop." → "Stop." (a command often follows other words).</summary>
    private static string LastSentence(string text) =>
        text.Split(['.', '!', '?', '¡', '¿'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? text;

    /// <summary>A short command made of stop words ("Stop.", "Para, para"), alone or as the last sentence.</summary>
    private static bool IsStop(string text) => IsStopWords(text) || IsStopWords(LastSentence(text));

    /// <summary>"…opened the folder stop": echo and the user's stop run together without punctuation.</summary>
    /// <summary>
    /// The user's own words in a turn heard over Clicky's voice, or null when it is all echo. A word is echo
    /// when it and a neighbour appear side by side in <paramref name="spoken"/>; the longest run of other words
    /// counts if it has 5+ words, most of which Clicky never said (fewer is likely misheard echo).
    /// </summary>
    internal static string? StripEcho(string turn, string spoken)
    {
        static string Norm(string w) => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var said = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(w => w.Length > 0).ToArray();
        var saidWords = said.ToHashSet();
        var saidPairs = said.Zip(said.Skip(1), (a, b) => (a, b)).ToHashSet();

        var words = turn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var norm = words.Select(Norm).ToArray();
        var echo = new bool[words.Length];
        for (int i = 0; i + 1 < words.Length; i++)
            if (saidPairs.Contains((norm[i], norm[i + 1]))) echo[i] = echo[i + 1] = true;

        int bestStart = 0, bestLen = 0;
        for (int i = 0, start = 0; i <= words.Length; i++)
        {
            if (i < words.Length && !echo[i]) continue;
            if (i - start > bestLen) (bestStart, bestLen) = (start, i - start);
            start = i + 1;
        }
        if (bestLen < 5) return null;
        var run = norm.Skip(bestStart).Take(bestLen).Where(w => w.Length > 0).ToArray();
        if (run.Count(saidWords.Contains) * 2 > run.Length) return null;
        return string.Join(' ', words, bestStart, bestLen).Trim(' ', ',', '.', '—', '-');
    }

    private static bool EndsWithStop(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() is { } w &&
        StopWords.Contains(w) && w is not ("wait" or "espera" or "alto");

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

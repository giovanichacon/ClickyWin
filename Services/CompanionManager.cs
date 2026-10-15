using System.Threading.Channels;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Orchestrates the full voice interaction loop:
/// hotkey press → audio capture → transcription → Claude → overlay + TTS
///
/// Each press starts a <see cref="VoiceSession"/>. Pressing again while Clicky is thinking or
/// speaking cancels the old session and starts listening right away. State changes coming
/// from a cancelled session are ignored, so a late "Idle" can never clobber "Listening".
///
/// Threading: press/release are cheap and may be called from the UI thread; all I/O and
/// CPU-heavy work runs on the thread pool. Events fire on arbitrary threads — subscribers
/// must marshal to the UI themselves (asynchronously).
/// </summary>
public sealed class CompanionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _mic;
    private readonly ScreenCaptureService _screen;
    private readonly ClaudeService _claude;
    private readonly SpeechService _tts;
    private readonly PointRefiner _pointer;
    private readonly ConversationHistory _history;

    private readonly object _gate = new();
    private VoiceSession? _current;
    private AppState _state = AppState.Idle;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short message when a pipeline stage fails.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires the instant a final transcript arrives — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;
    /// <summary>Question and spoken answer (for the tray panel and "copy answers").</summary>
    public event Action<string, string>? AnswerReady;
    /// <summary>The user asked Clicky to do something on the computer: (task, what they said).</summary>
    public event Action<string, string>? DoRequested;

    private CancellationTokenSource? _tutorCts;

    public CompanionManager(AppSettings settings, AudioCaptureService mic)
    {
        _settings = settings;
        _history = new ConversationHistory(settings.Config.Models.ConversationTurns);
        _mic = mic;
        _screen = new ScreenCaptureService(settings.Config.Screenshots);
        _claude = new ClaudeService(settings, _history);
        _tts = new SpeechService(settings);
        _pointer = new PointRefiner(_claude, _screen);
    }

    public AppState State
    {
        get { lock (_gate) return _state; }
    }

    // ── Push-to-talk lifecycle ──────────────────────────────────────────────

    public void OnPushToTalkPressed()
    {
        if (!_settings.HasRequiredKeys)
        {
            Logger.Log("[Hotkey] PRESSED but API keys are missing");
            FeedbackReceived?.Invoke(L.NeedKeys);
            return;
        }

        VoiceSession? previous;
        VoiceSession session;
        lock (_gate)
        {
            if (_state == AppState.Listening) return;
            previous = _current;
            session = new VoiceSession();
            _current = session;
            _state = AppState.Listening;
        }

        Logger.Log(previous == null ? "[Hotkey] PRESSED" : "[Hotkey] PRESSED — interrupting previous answer");
        StopTutor();
        previous?.Cancel();
        _tts.StopPlayback();
        StateChanged?.Invoke(AppState.Listening);

        session.Start(_settings, _mic, level => AudioLevelChanged?.Invoke(level));
        _ = WatchMicAsync(session);
    }

    /// <summary>Esc, or the shortcut turned out to be part of a Windows shortcut: drop the recording silently.</summary>
    public void OnPushToTalkCancelled()
    {
        VoiceSession? session;
        lock (_gate)
        {
            if (_state != AppState.Listening || _current == null) return;
            session = _current;
        }
        Logger.Log("[Hotkey] CANCELLED");
        session.Cancel();
        SetState(AppState.Idle, session);
        _ = session.DisposeAsync().AsTask();
    }

    /// <summary>True while a question is being recorded (dictation must not start then).</summary>
    public bool IsListening
    {
        get { lock (_gate) return _state == AppState.Listening; }
    }

    public void OnPushToTalkReleased()
    {
        VoiceSession session;
        lock (_gate)
        {
            if (_state != AppState.Listening || _current == null)
            {
                Logger.Log($"[Hotkey] RELEASED while {_state} — ignoring");
                return;
            }
            session = _current;
            _state = AppState.Processing;
        }

        Logger.Log("[Hotkey] RELEASED");
        StateChanged?.Invoke(AppState.Processing);
        _ = Task.Run(() => RunPipelineAsync(session));
    }

    private async Task WatchMicAsync(VoiceSession session)
    {
        if (await session.MicStarted.ConfigureAwait(false)) return;
        Feedback(session, L.NoMic);
        SetState(AppState.Idle, session);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    // ── Pipeline (thread pool) ──────────────────────────────────────────────

    private async Task RunPipelineAsync(VoiceSession s)
    {
        try
        {
            if (!await s.MicStarted.ConfigureAwait(false)) return;

            // Grab the screen while the transcript is being finalized
            var screenshotsTask = Task.Run(CaptureScreens);

            var (transcript, connectFailed) = await s.StopAndTranscribeAsync().ConfigureAwait(false);
            if (transcript.Length == 0)
            {
                Feedback(s, connectFailed ? L.NoConnect : L.NotHeard);
                return;
            }

            Logger.Log($"[Pipeline] Heard: \"{transcript}\"");
            L.NoteSpoken(transcript);
            if (!s.Token.IsCancellationRequested) TranscriptConfirmed?.Invoke();

            var screenshots = await screenshotsTask.ConfigureAwait(false);
            await ProcessResponseAsync(s, transcript, screenshots).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
        {
            Logger.Log("[Pipeline] Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Pipeline] Unexpected error: {ex}");
            Feedback(s, L.NoAnswer);
        }
        finally
        {
            await s.DisposeAsync().ConfigureAwait(false);
            SetState(AppState.Idle, s);
        }
    }

    private List<ScreenshotResult> CaptureScreens()
    {
        try
        {
            var shots = _screen.CaptureAll();
            Logger.Log($"[Screen] Captured {shots.Count} display(s)");
            return shots;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Screen] Capture failed: {ex.Message}");
            return [];
        }
    }

    // ── Claude + TTS ────────────────────────────────────────────────────────

    /// <summary>
    /// Streams the reply and speaks it sentence by sentence while the rest is still arriving;
    /// flies the cursor to a [POINT] as soon as its tag is complete.
    /// </summary>
    private async Task ProcessResponseAsync(VoiceSession s, string transcript, List<ScreenshotResult> screenshots)
    {
        var reply = new System.Text.StringBuilder();
        var info = new ReplyInfo();
        bool pointed = false;

        var sentences = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        // Speaking starts when audio literally starts, so the spinner covers the TTS fetch
        var speaking = _tts.SpeakSentencesAsync(sentences.Reader, () => SetState(AppState.Speaking, s), s.Token);
        var splitter = new SentenceSplitter();
        bool claudeFailed = false;
        bool doFired = false;

        try
        {
            await foreach (var chunk in _claude.StreamReplyAsync(transcript, screenshots, info, s.Token).ConfigureAwait(false))
            {
                reply.Append(chunk);
                foreach (var sentence in splitter.Add(chunk))
                    sentences.Writer.TryWrite(sentence);

                if (!pointed && TryPoint(reply.ToString(), screenshots, s.Token))
                    pointed = true;

                // [DO: task] comes first: open the Allow card while the reply is still being spoken
                if (!doFired && ClaudeService.ParseDoTask(reply.ToString()) is { } task)
                {
                    doFired = true;
                    Logger.Log($"[Claude] DO \"{task}\"");
                    DoRequested?.Invoke(task, transcript);
                }
            }
            foreach (var sentence in splitter.Flush())
                sentences.Writer.TryWrite(sentence);
        }
        catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
        {
            sentences.Writer.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Claude API error: {ex.Message}");
            claudeFailed = true;
        }
        sentences.Writer.TryComplete();

        var fullText = ClaudeService.StripPointTags(reply.ToString());
        Logger.Log($"[Claude] Reply ({info.Model}{(info.Escalated ? ", escalated" : "")}): \"{fullText}\"");

        try
        {
            await speaking.ConfigureAwait(false);
            if (fullText.Length > 0) Logger.Log("[TTS] Playback complete");
        }
        catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"TTS failed: {ex.Message}");
            // Claude responded but we can't play it — show the start of the answer as text
            if (fullText.Length > 0)
                Feedback(s, fullText.Length > 30 ? fullText[..30].TrimEnd() + "…" : fullText);
        }

        if (claudeFailed && fullText.Length == 0) Feedback(s, L.NoAnswer);
        if (fullText.Length == 0)
        {
            if (info.Refused) Feedback(s, L.Refused);
            return;
        }

        AnswerReady?.Invoke(transcript, fullText);

        if (doFired) return;

        // Tutor mode: a reply that pointed somewhere is a step; keep watching for the next one
        if (pointed && _settings.TutorMode && !s.Token.IsCancellationRequested)
            StartTutor(transcript);
    }

    private bool TryPoint(string replySoFar, List<ScreenshotResult> screenshots, CancellationToken ct)
    {
        if (screenshots.Count == 0 || ClaudeService.ParsePoints(replySoFar) is not [var point, ..]) return false;
        var shot = screenshots.FirstOrDefault(x => x.Number == point.Screen) ?? screenshots[0];
        var (x, y) = shot.ToPhysical(point.X, point.Y);
        Logger.Log($"[Claude] POINT \"{point.Label}\" screen {shot.Number} ({point.X},{point.Y}) → physical ({x:F0},{y:F0})");
        _ = PointAsync(x, y, point.Label, ClaudeService.StripPointTags(replySoFar), shot.Bounds, ct);
        return true;
    }

    /// <summary>Snaps the guess onto the real element (UI Automation, else a zoomed look), then flies there; skips a wrong guess.</summary>
    private async Task PointAsync(double x, double y, string label, string description,
        System.Drawing.Rectangle screenBounds, CancellationToken ct)
    {
        try
        {
            if (await _pointer.RefineAsync(x, y, label, description, screenBounds, ct).ConfigureAwait(false) is { } p &&
                !ct.IsCancellationRequested)
                PointReceived?.Invoke(p.x, p.y, label);
        }
        catch (OperationCanceledException) { }
    }

    // ── Tutor mode ──────────────────────────────────────────────────────────
    // After a step, watch the screen (tiny thumbnails, no API calls) and, once it changes and
    // settles, ask the everyday model for the next step. Ends on [DONE], after a few minutes or
    // checks, or as soon as the user presses any Clicky shortcut.

    private static readonly TimeSpan TutorDuration = TimeSpan.FromMinutes(3);
    private const int TutorMaxChecks = 12;

    private void StartTutor(string goal)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _tutorCts, cts)?.Cancel();
        FeedbackReceived?.Invoke(L.TutorWatching);
        _ = Task.Run(() => RunTutorAsync(goal, cts.Token));
    }

    public void StopTutor() => Interlocked.Exchange(ref _tutorCts, null)?.Cancel();

    private async Task RunTutorAsync(string goal, CancellationToken ct)
    {
        Logger.Log("[Tutor] Watching");
        var until = DateTime.UtcNow + TutorDuration;
        int checks = 0;
        try
        {
            var last = ScreenCaptureService.Thumbnail();
            while (DateTime.UtcNow < until && checks < TutorMaxChecks)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                var now = ScreenCaptureService.Thumbnail();
                if (!ScreenCaptureService.Differs(last, now)) continue;

                // Wait for the screen to settle (menus opening, pages loading)
                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    var next = ScreenCaptureService.Thumbnail();
                    bool settled = !ScreenCaptureService.Differs(now, next);
                    now = next;
                    if (settled) break;
                }
                last = now;
                checks++;
                lock (_gate)
                    if (_current != null) return; // the user is asking something else

                if (await TutorStepAsync(goal, ct).ConfigureAwait(false)) return;
                until = DateTime.UtcNow + TutorDuration; // progress made: keep going
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Log($"[Tutor] Stopped: {ex.Message}");
        }
        finally
        {
            Logger.Log($"[Tutor] Done after {checks} check(s)");
        }
    }

    /// <summary>One check. Returns true when the lesson is over.</summary>
    private async Task<bool> TutorStepAsync(string goal, CancellationToken ct)
    {
        var shots = CaptureScreens();
        if (shots.Count == 0) return true;

        var system = _settings.Config.Prompts.SystemText + "\n\n" + _settings.Config.Prompts.TutorText +
                     $"\nThe user's goal: {goal}";
        var info = new ReplyInfo();
        var reply = (await _claude.CompleteAsync(system, ClaudeService.BuildUserContent("(The screen changed.)", shots),
            info, allowEscalation: false, ct, _history.Turns).ConfigureAwait(false)).Trim();

        if (reply.Length == 0 || reply.StartsWith("[WAIT", StringComparison.OrdinalIgnoreCase)) return false;
        bool done = reply.StartsWith("[DONE]", StringComparison.OrdinalIgnoreCase);
        if (done) reply = reply["[DONE]".Length..];

        TryPoint(reply, shots, ct);
        var text = ClaudeService.StripPointTags(reply);
        Logger.Log($"[Tutor] {(done ? "Done" : "Next step")}: \"{text}\"");
        _history.AddUserMessage("(The screen changed.)");
        _history.AddAssistantMessage(text);

        if (text.Length > 0)
        {
            await _tts.SpeakAsync(text, () => SetTutorState(AppState.Speaking), ct).ConfigureAwait(false);
            SetTutorState(AppState.Idle);
            AnswerReady?.Invoke(goal, text);
        }
        return done;
    }

    /// <summary>State changes from the tutor never override a question in progress.</summary>
    private void SetTutorState(AppState state)
    {
        lock (_gate)
        {
            if (_current != null || _state == state) return;
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    // ── State helpers ───────────────────────────────────────────────────────

    /// <summary>Applies a state change only if <paramref name="owner"/> is still the active session.</summary>
    private void SetState(AppState state, VoiceSession owner)
    {
        lock (_gate)
        {
            if (owner != _current || _state == state) return;
            _state = state;
            if (state == AppState.Idle) _current = null;
        }
        Logger.Log($"[State] → {state}");
        StateChanged?.Invoke(state);
    }

    private void Feedback(VoiceSession owner, string message)
    {
        if (!owner.Token.IsCancellationRequested)
            FeedbackReceived?.Invoke(message);
    }

    // ── Computer use ────────────────────────────────────────────────────────

    /// <summary>Runs a typed question through the spoken-question pipeline (testing: --ask "text").</summary>
    public void AskText(string transcript)
    {
        VoiceSession s = new();
        lock (_gate)
        {
            _current = s;
            _state = AppState.Processing;
        }
        StateChanged?.Invoke(AppState.Processing);
        _ = Task.Run(async () =>
        {
            try
            {
                Logger.Log($"[Pipeline] Typed: \"{transcript}\"");
                await ProcessResponseAsync(s, transcript, CaptureScreens()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Pipeline] Unexpected error: {ex}");
            }
            finally
            {
                await s.DisposeAsync().ConfigureAwait(false);
                SetState(AppState.Idle, s);
            }
        });
    }

    /// <summary>Shows the cursor while Clicky works (Speaking) and hides it after (Idle); never overrides a question.</summary>
    public void SetAgentState(AppState state) => SetTutorState(state);

    /// <summary>Speaks a short line (computer-use result) unless the user is asking something.</summary>
    public async Task SayAsync(string text, CancellationToken ct = default)
    {
        if (text.Length == 0) return;
        try
        {
            await _tts.SpeakAsync(text, () => SetTutorState(AppState.Speaking), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"TTS failed: {ex.Message}");
            FeedbackReceived?.Invoke(text);
        }
        finally
        {
            SetTutorState(AppState.Idle);
        }
    }

    public void StopSpeaking()
    {
        StopTutor();
        _tts.StopPlayback();
    }

    public async ValueTask DisposeAsync()
    {
        VoiceSession? current;
        lock (_gate) current = _current;
        if (current != null)
            await current.DisposeAsync().ConfigureAwait(false);
        _tts.Dispose();
    }
}

using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Orchestrates the full voice interaction loop:
/// hotkey press → audio capture → transcription → Claude → overlay + TTS
///
/// Each press starts a <see cref="Session"/>. Pressing again while Clicky is thinking or
/// speaking cancels the old session and starts listening right away. State changes coming
/// from a cancelled session are ignored, so a late "Idle" can never clobber "Listening".
///
/// Threading: press/release are cheap and may be called from the UI thread; all I/O and
/// CPU-heavy work runs on the thread pool. Events fire on arbitrary threads — subscribers
/// must marshal to the UI themselves (asynchronously).
/// </summary>
public sealed class CompanionManager : IAsyncDisposable
{
    // Spoken-style feedback shown in the overlay bubble. Localized in phase 5.
    private const string MsgNoMic       = "I can't hear your microphone";
    private const string MsgNoConnect   = "I couldn't connect";
    private const string MsgNotHeard    = "Sorry, I didn't catch that";
    private const string MsgNoAnswer    = "Sorry, I couldn't answer";

    private static readonly TimeSpan TranscriptTimeout = TimeSpan.FromSeconds(6);

    private readonly AppSettings _settings;
    private readonly AudioCaptureService _mic;
    private readonly ScreenCaptureService _screen;
    private readonly ClaudeService _claude;
    private readonly ElevenLabsService _tts;
    private readonly ConversationHistory _history;

    private readonly object _gate = new();
    private Session? _current;
    private AppState _state = AppState.Idle;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short message when a pipeline stage fails.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires the instant a final transcript arrives — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;

    public CompanionManager(AppSettings settings)
    {
        _settings = settings;
        _history = new ConversationHistory(maxTurns: 10);
        _mic = new AudioCaptureService();
        _screen = new ScreenCaptureService();
        _claude = new ClaudeService(settings, _history);
        _tts = new ElevenLabsService(settings);
    }

    public AppState State
    {
        get { lock (_gate) return _state; }
    }

    // ── Push-to-talk lifecycle ──────────────────────────────────────────────

    public void OnPushToTalkPressed()
    {
        Session? previous;
        Session session;
        lock (_gate)
        {
            if (_state == AppState.Listening) return;
            previous = _current;
            session = new Session();
            _current = session;
            _state = AppState.Listening;
        }

        Logger.Log(previous == null ? "[Hotkey] PRESSED" : "[Hotkey] PRESSED — interrupting previous answer");
        previous?.Cancel();
        _tts.StopPlayback();
        StateChanged?.Invoke(AppState.Listening);

        // The transcription session exists before the mic starts, so every frame has a
        // destination; frames queue up (pre-roll) until the WebSocket is open.
        session.Asr = new AssemblyAIService(_settings.AssemblyAiApiKey, _settings.AssemblyAiSpeechModel);
        session.AsrConnected = session.Asr.ConnectAsync();
        session.MicStarted = Task.Run(() => StartMicAsync(session));
    }

    /// <summary>Opens and briefly runs the mic so the first press starts fast (mic indicator blinks once).</summary>
    public Task WarmUpMicAsync() => Task.Run(_mic.WarmUpAsync);

    public void OnPushToTalkReleased()
    {
        Session session;
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

    private async Task<bool> StartMicAsync(Session session)
    {
        var recording = await _mic.StartAsync(
            session.Asr!.EnqueueAudio, level => AudioLevelChanged?.Invoke(level)).ConfigureAwait(false);

        if (recording == null)
        {
            Feedback(session, MsgNoMic);
            SetState(AppState.Idle, session);
            _ = session.DisposeAsync().AsTask();
            return false;
        }

        session.Mic = recording;
        Logger.Log($"[Audio] Recording {System.Diagnostics.Stopwatch.GetElapsedTime(session.PressedAt).TotalMilliseconds:F0} ms after press");
        return true;
    }

    // ── Pipeline (thread pool) ──────────────────────────────────────────────

    private async Task RunPipelineAsync(Session s)
    {
        try
        {
            if (!await s.MicStarted.ConfigureAwait(false)) return;
            await s.Mic!.StopAsync().ConfigureAwait(false);

            // Grab the screen while the transcript is being finalized
            var screenshotsTask = Task.Run(CaptureScreens);

            string transcript = await s.Asr!.FinishAsync(TranscriptTimeout, s.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await s.AsrConnected.ConfigureAwait(false);
                Feedback(s, s.Asr.Error != null ? MsgNoConnect : MsgNotHeard);
                return;
            }

            Logger.Log($"[Pipeline] Heard: \"{transcript}\"");
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
            Feedback(s, MsgNoAnswer);
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

    private async Task ProcessResponseAsync(Session s, string transcript, List<ScreenshotResult> screenshots)
    {
        Logger.Log($"[Claude] Sending transcript with {screenshots.Count} screenshot(s)");

        var responseBuilder = new System.Text.StringBuilder();
        PointTarget? detectedPoint = null;

        try
        {
            await foreach (var chunk in _claude.StreamResponseAsync(transcript, screenshots, s.Token).ConfigureAwait(false))
            {
                responseBuilder.Append(chunk);

                // Capture the first POINT tag label for CU refinement — don't fire yet
                if (detectedPoint == null)
                {
                    var pts = ClaudeService.ParsePoints(responseBuilder.ToString());
                    if (pts.Count > 0)
                        detectedPoint = pts[0];
                }
            }
        }
        catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"Claude API error: {ex.Message}");
            Feedback(s, MsgNoAnswer);
            return;
        }

        // Two-phase point detection: use Computer Use API for precise coordinates
        if (detectedPoint != null && screenshots.Count > 0)
        {
            Logger.Log($"[Claude] POINT detected: label=\"{detectedPoint.Label}\" — starting CU coordinate refinement");
            (double x, double y) target = (detectedPoint.X, detectedPoint.Y);

            try
            {
                var primaryShot = screenshots[0]; // cursor's screen (sorted first)
                var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(
                    primaryShot.Bounds.Width, primaryShot.Bounds.Height);

                var resized = _screen.CaptureResized(primaryShot.Bounds, cuW, cuH);
                if (resized != null)
                {
                    var (physX, physY) = await _claude.DetectElementAsync(
                        resized.Base64, detectedPoint.Label,
                        primaryShot.Bounds.Width, primaryShot.Bounds.Height,
                        s.Token).ConfigureAwait(false);

                    if (physX >= 0)
                    {
                        Logger.Log($"[CU] Precise POINT: ({physX},{physY})");
                        target = (physX, physY);
                    }
                    else
                    {
                        Logger.Log("[CU] No tool_use coordinate in response — using rough coords");
                    }
                }
            }
            catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"[CU] Computer Use detection failed: {ex.Message}");
            }

            if (!s.Token.IsCancellationRequested)
                PointReceived?.Invoke(target.x, target.y, detectedPoint.Label);
        }

        var fullText = ClaudeService.StripPointTags(responseBuilder.ToString());
        Logger.Log($"[Claude] Response text: \"{fullText}\"");
        if (string.IsNullOrWhiteSpace(fullText)) return;

        try
        {
            // Speaking starts when audio literally starts, so the spinner covers the TTS fetch
            await _tts.SpeakAsync(fullText, () => SetState(AppState.Speaking, s), s.Token).ConfigureAwait(false);
            Logger.Log("[TTS] Playback complete");
        }
        catch (OperationCanceledException) when (s.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"TTS failed: {ex.Message}");
            // Claude responded but we can't play it — show the start of the answer as text
            Feedback(s, fullText.Length > 30 ? fullText[..30].TrimEnd() + "…" : fullText);
        }
    }

    // ── State helpers ───────────────────────────────────────────────────────

    /// <summary>Applies a state change only if <paramref name="owner"/> is still the active session.</summary>
    private void SetState(AppState state, Session owner)
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

    private void Feedback(Session owner, string message)
    {
        if (!owner.Token.IsCancellationRequested)
            FeedbackReceived?.Invoke(message);
    }

    public void StopSpeaking() => _tts.StopPlayback();

    public async ValueTask DisposeAsync()
    {
        Session? current;
        lock (_gate) current = _current;
        if (current != null)
            await current.DisposeAsync().ConfigureAwait(false);
        _tts.Dispose();
        _mic.Dispose();
    }

    /// <summary>One press-to-answer interaction. Owns the mic, the ASR socket and the cancellation.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        public long PressedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();

        public CancellationToken Token => _cts.Token;
        public AudioCaptureService.Recording? Mic { get; set; }
        public AssemblyAIService? Asr { get; set; }
        public Task<bool> MicStarted { get; set; } = Task.FromResult(false);
        public Task AsrConnected { get; set; } = Task.CompletedTask;

        public void Cancel() => _cts.Cancel();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _cts.Cancel();
            try
            {
                if (await MicStarted.ConfigureAwait(false) && Mic != null)
                    await Mic.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"[Session] Mic cleanup error: {ex.Message}");
            }
            try
            {
                if (Asr != null)
                    await Asr.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"[Session] ASR cleanup error: {ex.Message}");
            }
            // _cts is not disposed: late callbacks may still check Token
        }
    }
}

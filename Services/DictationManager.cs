using System.Windows.Threading;
using Anthropic.Models.Beta.Messages;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public enum DictationMode { Dictate, Write, Prompt }

public sealed record DictationEntry(DateTime At, DictationMode Mode, string Text, string App);

/// <summary>
/// Voice typing, in three modes:
/// - Dictate: speech → (optional) faithful cleanup by the everyday model → typed at the caret.
/// - Prompt: think-out-loud speech → rewritten as a concise AI prompt (repeats merged, final intent kept).
/// - Write: "reply that I'll be there at 5" → the screen, the focused box's text and selection
///   (UI Automation) and the request go to Claude → its text is typed at the caret, replacing
///   any selection.
/// Words the user fixes within ~20 s of a dictation are learned into the personal dictionary.
///
/// Threading: Start/Finish/Cancel are cheap and thread-safe; the pipeline runs on the thread pool.
/// Events fire on arbitrary threads.
/// </summary>
public sealed class DictationManager : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LearnDelay = TimeSpan.FromSeconds(20);
    private const int HistorySize = 20;

    private readonly AppSettings _settings;
    private readonly AudioCaptureService _mic;
    private readonly ScreenCaptureService _screen;
    private readonly ClaudeService _claude;
    private readonly Dispatcher _ui;

    private readonly object _gate = new();
    private Run? _current;
    private readonly LinkedList<DictationEntry> _recent = new();

    public event Action<AppState>? StateChanged;
    public event Action<float>? AudioLevelChanged;
    public event Action<string>? FeedbackReceived;
    public event Action<DictationEntry>? Inserted;

    public DictationManager(AppSettings settings, AudioCaptureService mic, Dispatcher ui)
    {
        _settings = settings;
        _mic = mic;
        _ui = ui;
        _screen = new ScreenCaptureService(settings.Config.Screenshots);
        _claude = new ClaudeService(settings, new ConversationHistory(0));
    }

    public bool IsBusy
    {
        get { lock (_gate) return _current != null; }
    }

    public IReadOnlyList<DictationEntry> Recent
    {
        get { lock (_recent) return _recent.ToList(); }
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public void Start(DictationMode mode)
    {
        if (!_settings.HasRequiredKeys)
        {
            FeedbackReceived?.Invoke(L.NeedKeys);
            return;
        }

        var run = new Run(mode);
        lock (_gate)
        {
            if (_current != null) return;
            _current = run;
        }

        Logger.Log($"[{mode}] PRESSED");
        StateChanged?.Invoke(AppState.Listening);
        run.Session.Start(_settings, _mic, level => AudioLevelChanged?.Invoke(level));

        // Read the target now, while the user talks: by release it is ready
        bool readBox = mode == DictationMode.Write || _settings.LearnWords;
        run.Focus = FocusReader.CaptureAsync(readBox, TimeSpan.FromSeconds(1.5));
        if (mode == DictationMode.Write)
            run.Screens = Task.Run(CaptureScreens);

        _ = WatchMicAsync(run);
    }

    public void Finish()
    {
        Run? run;
        lock (_gate)
        {
            run = _current;
            if (run == null || run.Finishing) return;
            run.Finishing = true;
        }
        Logger.Log($"[{run.Mode}] RELEASED");
        StateChanged?.Invoke(AppState.Processing);
        _ = Task.Run(() => RunPipelineAsync(run));
    }

    public void Cancel()
    {
        Run? run;
        lock (_gate)
        {
            run = _current;
            _current = null;
        }
        if (run == null) return;
        Logger.Log($"[{run.Mode}] CANCELLED");
        run.Session.Cancel();
        _ = run.Session.DisposeAsync().AsTask();
        StateChanged?.Invoke(AppState.Idle);
    }

    public void NotifyHandsFree() => FeedbackReceived?.Invoke(L.HandsFree);

    private async Task WatchMicAsync(Run run)
    {
        if (await run.Session.MicStarted.ConfigureAwait(false)) return;
        if (!run.Session.Token.IsCancellationRequested) FeedbackReceived?.Invoke(L.NoMic);
        End(run);
        await run.Session.DisposeAsync().ConfigureAwait(false);
    }

    private void End(Run run)
    {
        lock (_gate)
        {
            if (_current != run) return;
            _current = null;
        }
        StateChanged?.Invoke(AppState.Idle);
    }

    // ── Pipeline (thread pool) ──────────────────────────────────────────────

    private async Task RunPipelineAsync(Run run)
    {
        var ct = run.Session.Token;
        try
        {
            if (!await run.Session.MicStarted.ConfigureAwait(false)) return;

            var (transcript, connectFailed) = await run.Session.StopAndTranscribeAsync().ConfigureAwait(false);
            if (transcript.Length == 0)
            {
                Feedback(run, connectFailed ? L.NoConnect : L.NotHeard);
                return;
            }
            Logger.Log($"[{run.Mode}] Heard: \"{transcript}\"");
            L.NoteSpoken(transcript);

            var focus = await run.Focus.ConfigureAwait(false);
            string text = run.Mode == DictationMode.Write
                ? await WriteAsync(transcript, focus, await run.Screens.ConfigureAwait(false), ct).ConfigureAwait(false)
                : await CleanUpAsync(run.Mode, transcript, focus, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                Feedback(run, L.NoWriteText);
                return;
            }
            ct.ThrowIfCancellationRequested();

            // Dictation: trailing space so the next dictation doesn't glue onto this one
            if (run.Mode == DictationMode.Dictate && !char.IsWhiteSpace(text[^1])) text += " ";

            var result = await TextInserter.InsertAsync(text, _ui).ConfigureAwait(false);
            Logger.Log($"[{run.Mode}] {result} into {focus.Describe()}: \"{text.Trim()}\"");
            if (result == InsertResult.CopiedOnly) Feedback(run, L.CopiedPaste);

            var entry = new DictationEntry(DateTime.Now, run.Mode, text.Trim(), focus.App);
            lock (_recent)
            {
                _recent.AddFirst(entry);
                while (_recent.Count > HistorySize) _recent.RemoveLast();
            }
            Inserted?.Invoke(entry);

            if (run.Mode == DictationMode.Dictate && _settings.LearnWords && focus.Element != null &&
                result != InsertResult.CopiedOnly)
                _ = LearnAsync(focus.Element, text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Log($"[{run.Mode}] Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error($"[{run.Mode}] Unexpected error: {ex.Message}");
            Feedback(run, L.NoAnswer);
        }
        finally
        {
            await run.Session.DisposeAsync().ConfigureAwait(false);
            End(run);
        }
    }

    private List<ScreenshotResult> CaptureScreens()
    {
        try { return _screen.CaptureAll(); }
        catch (Exception ex)
        {
            Logger.Log($"[Write] Screen capture failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Dictate: faithful cleanup. Prompt: rewrite into a concise AI prompt (always on).
    /// Falls back to the raw transcript if the model is slow, fails or "answers".
    /// </summary>
    private async Task<string> CleanUpAsync(DictationMode mode, string transcript, FocusContext focus, CancellationToken ct)
    {
        bool prompt = mode == DictationMode.Prompt;
        if (!prompt && !_settings.DictationCleanup) return transcript;

        var system = prompt ? _settings.Config.Prompts.PromptText : _settings.Config.Prompts.DictationText;
        if (_settings.Dictionary.Count > 0)
            system += "\n\nUser's dictionary: " + string.Join(", ", _settings.Dictionary);
        if (!string.IsNullOrEmpty(focus.App))
            system += $"\nThe text will be typed into: {focus.Describe()}.";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(prompt ? PromptTimeout : CleanupTimeout);
        try
        {
            var cleaned = (await _claude.CompleteAsync(system,
                [new BetaTextBlockParam { Text = $"<transcript>{transcript}</transcript>" }],
                new ReplyInfo(), allowEscalation: false, timeout.Token).ConfigureAwait(false)).Trim();

            // A reply much longer than what was said means the model answered instead of cleaning
            int limit = prompt ? transcript.Length * 3 / 2 + 80 : transcript.Length * 2 + 40;
            if (cleaned.Length == 0 || cleaned.Length > limit)
            {
                Logger.Log($"[{mode}] Cleanup output rejected: \"{cleaned}\"");
                return transcript;
            }
            return cleaned;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Log($"[{mode}] Cleanup timed out — using the raw transcript");
            return transcript;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log($"[{mode}] Cleanup failed ({ex.Message}) — using the raw transcript");
            return transcript;
        }
    }

    private async Task<string> WriteAsync(string request, FocusContext focus, List<ScreenshotResult> screens,
        CancellationToken ct)
    {
        var context = new System.Text.StringBuilder();
        context.AppendLine($"App: {focus.Describe()}");
        if (focus.Selection.Length > 0)
            context.AppendLine($"Selected text (your output replaces it):\n<selection>\n{focus.Selection}\n</selection>");
        if (focus.FieldText.Length > 0)
            context.AppendLine($"Current text in the box:\n<box>\n{focus.FieldText}\n</box>");
        context.Append($"Spoken request: {request}");

        var info = new ReplyInfo();
        var text = await _claude.CompleteAsync(_settings.Config.Prompts.WriteText,
            ClaudeService.BuildUserContent(context.ToString(), screens), info, allowEscalation: true, ct)
            .ConfigureAwait(false);
        Logger.Log($"[Write] {info.Model}{(info.Escalated ? " (escalated)" : "")} wrote {text.Length} chars");
        return info.Refused ? "" : ClaudeService.StripPointTags(text).Trim();
    }

    private async Task LearnAsync(System.Windows.Automation.AutomationElement element, string inserted)
    {
        await Task.Delay(LearnDelay).ConfigureAwait(false);
        var final = await FocusReader.ReadTextAsync(element, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (final == null) return;

        var learned = WordLearner.FindCorrections(inserted, final)
            .Where(w => !_settings.Dictionary.Contains(w, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (learned.Count == 0) return;

        _settings.Dictionary.AddRange(learned);
        if (_settings.Dictionary.Count > 100) _settings.Dictionary.RemoveRange(0, _settings.Dictionary.Count - 100);
        try { _settings.Save(); }
        catch (Exception ex) { Logger.Log($"[Learn] Could not save the dictionary: {ex.Message}"); }

        Logger.Log($"[Learn] Added to dictionary: {string.Join(", ", learned)}");
        FeedbackReceived?.Invoke(L.LearnedWord(string.Join(", ", learned)));
    }

    private void Feedback(Run run, string message)
    {
        if (!run.Session.Token.IsCancellationRequested) FeedbackReceived?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        Run? run;
        lock (_gate) run = _current;
        if (run != null) await run.Session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Run(DictationMode mode)
    {
        public DictationMode Mode { get; } = mode;
        public VoiceSession Session { get; } = new();
        public Task<FocusContext> Focus { get; set; } = Task.FromResult(new FocusContext(0, "", "", "", "", null));
        public Task<List<ScreenshotResult>> Screens { get; set; } = Task.FromResult(new List<ScreenshotResult>());
        public bool Finishing { get; set; }
    }
}

using System.Globalization;
using System.Text;
using System.Threading.Channels;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Teacher mode: a paced, step-by-step lesson started by a [TEACH: goal] reply. Each step is said and drawn
/// (POINT/CIRCLE/DRAG/KEYS), then Clicky waits for a voice command: next, again, show me (Clicky performs the
/// step's tags with the real cursor, slowly), let me try (undo the demo, redraw, watch), slower, why, done,
/// stop. While waiting, a screen change (tiny thumbnails, no API) triggers one cheap check; a finished step
/// moves on. Commands run one at a time on a single loop; a new voice command cuts off the one in progress.
/// Steps use the step model (Sonnet, exact coordinates), checks the check model (Haiku); cost-capped per lesson.
/// </summary>
public sealed class TeacherLesson(AppSettings settings, ClaudeService claude, ScreenCaptureService screen,
    Func<string, CancellationToken, Task> say, Action stopVoice, Action<double, double, string> point)
{
    public enum Command { Next, Again, ShowMe, LetMeTry, Slower, Why, Done, Hush, Stop, Check }

    private sealed record Step(string Text, List<TeachMark> Marks, bool Undo);

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    private const int MaxChecksPerStep = 6;
    private const int MaxTurns = 12;

    private readonly object _gate = new();
    private Channel<Command>? _commands;
    private CancellationTokenSource? _lessonCts, _actCts;
    private DateTime _lastActivity;

    // Touched only by the lesson loop (one command at a time)
    private readonly List<ConversationTurn> _turns = [];
    private string _goal = "";
    private Step? _step;
    private int _steps, _checks;
    private double _slowness = 1, _spent;
    private bool _trying, _undoPending, _slower;

    // Screen watcher: armed by the loop, disarmed by a voice command or when a change is posted
    private volatile bool _watching;
    private int _watchGen;

    public bool IsActive
    {
        get { lock (_gate) return _commands != null; }
    }

    /// <summary>Drawings for the current step (physical pixels) and the slowness factor; raised on a worker thread.</summary>
    public event Action<IReadOnlyList<TeachMark>, double>? MarksChanged;
    public event Action? MarksCleared;

    private TeacherConfig Config => settings.Config.Teacher;

    public void Start(string goal)
    {
        Stop("new lesson");
        var cts = new CancellationTokenSource();
        var commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            _commands = commands;
            _lessonCts = cts;
            _lastActivity = DateTime.UtcNow;
        }
        Logger.Log($"[Teach] Lesson: \"{goal}\"");
        commands.Writer.TryWrite(Command.Next);
        _ = Task.Run(() => RunAsync(goal, commands.Reader, cts));
        _ = Task.Run(() => WatchAsync(cts));
    }

    /// <param name="owner">Only stop if this is still the running lesson (a finished loop must not end a newer one).</param>
    public void Stop(string reason, CancellationTokenSource? owner = null)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_commands == null || owner != null && owner != _lessonCts) return;
            _commands.Writer.TryComplete();
            _commands = null;
            cts = _lessonCts;
            _lessonCts = null;
        }
        _watching = false;
        cts?.Cancel();
        Logger.Log($"[Teach] Ended ({reason})");
        MarksCleared?.Invoke();
    }

    /// <summary>
    /// A voice command for the lesson ("next", "show me", "¿por qué?"). Returns false when no lesson runs or the
    /// words aren't a command, so they go on to the normal pipeline.
    /// </summary>
    /// <param name="exact">Heard over Clicky's own voice: only a whole-turn command counts (echo is full sentences).</param>
    public bool TryCommand(string text, bool exact = false)
    {
        if (!IsActive || Parse(text, exact) is not { } cmd) return false;
        Logger.Log($"[Teach] {cmd}: \"{text}\"");
        if (cmd == Command.Stop)
        {
            stopVoice();
            Stop("voice");
            _ = say(L.TeachEnded, CancellationToken.None);
            return true;
        }
        Post(cmd, interrupt: true);
        return true;
    }

    private void Post(Command cmd, bool interrupt)
    {
        lock (_gate)
        {
            if (_commands == null) return;
            _lastActivity = DateTime.UtcNow;
            if (interrupt)
            {
                _watching = false;
                _actCts?.Cancel(); // inside the lock: the loop disposes it under the same lock
            }
            _commands.Writer.TryWrite(cmd);
        }
        if (interrupt) stopVoice();
    }

    // ── Lesson loop ─────────────────────────────────────────────────────────

    private async Task RunAsync(string goal, ChannelReader<Command> commands, CancellationTokenSource lesson)
    {
        var ct = lesson.Token;
        _goal = goal;
        _step = null;
        _steps = 0;
        _slowness = 1;
        _spent = 0;
        _slower = false;
        _turns.Clear();
        try
        {
            await foreach (var cmd in commands.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var act = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_gate) _actCts = act;
                try
                {
                    if (!await HandleAsync(cmd, act.Token).ConfigureAwait(false))
                    {
                        Stop("finished", lesson);
                        return;
                    }
                    if (cmd is not (Command.ShowMe or Command.Hush) && _step != null) Watch();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Logger.Log($"[Teach] {cmd} cut off");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error($"[Teach] {cmd} failed: {ex.Message}");
                    await say(L.TeachFailed, ct).ConfigureAwait(false); // the lesson waits: "again" or "next" retries
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_actCts == act) _actCts = null;
                        act.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        Logger.Log($"[Teach] {_steps} step(s), ≈ ${_spent:F3}");
    }

    /// <summary>Runs one command. Returns false when the lesson is over.</summary>
    private async Task<bool> HandleAsync(Command cmd, CancellationToken ct)
    {
        if (cmd != Command.Next && cmd != Command.Hush && _step == null) cmd = Command.Next;
        switch (cmd)
        {
            case Command.Next:
                return await NextStepAsync(ct).ConfigureAwait(false);
            case Command.Again:
                await PresentAsync(_step!, ct).ConfigureAwait(false);
                return true;
            case Command.Slower:
                _slowness = Math.Min(_slowness * 1.5, 3);
                _slower = true;
                await PresentAsync(_step!, ct).ConfigureAwait(false);
                return true;
            case Command.Why:
                await WhyAsync(_step!, ct).ConfigureAwait(false);
                return true;
            case Command.ShowMe:
                await ShowMeAsync(_step!, ct).ConfigureAwait(false);
                return true;
            case Command.LetMeTry:
                await LetMeTryAsync(_step!, ct).ConfigureAwait(false);
                return true;
            case Command.Done:
                _trying = true;
                return await CheckAsync(_step!, ct).ConfigureAwait(false);
            case Command.Check:
                return await CheckAsync(_step!, ct).ConfigureAwait(false);
            default: // Hush: the interrupt already silenced Clicky
                return true;
        }
    }

    private string SystemPrompt() =>
        "You are Clicky, a friendly assistant next to the user's cursor on Windows. Your replies are read aloud.\n" +
        settings.Config.Prompts.TeacherText + $"\nThe goal: {_goal}\nSpeak {L.CurrentLanguageName}.";

    private bool OverBudget() => _spent >= Config.CostCap;

    private async Task<bool> NextStepAsync(CancellationToken ct)
    {
        if (OverBudget())
        {
            await Say(L.TeachCap, ct).ConfigureAwait(false);
            return false;
        }
        if (_steps >= Config.MaxSteps)
        {
            await Say(L.TeachEnded, ct).ConfigureAwait(false);
            return false;
        }
        MarksCleared?.Invoke();
        if (Capture() is not { } shot) return false;

        var request = (_steps == 0 ? "Give the first step." : "Give the next step.") +
                      (_slower ? " The user asked to go slower: smaller steps, simpler words." : "");
        var info = new ReplyInfo();
        var reply = (await claude.CompleteAsync(SystemPrompt(), ClaudeService.BuildUserContent(request, [shot]), info,
            allowEscalation: false, ct, _turns, Config.StepModel).ConfigureAwait(false)).Trim();
        _spent += info.Cost;

        bool done = reply.Contains("[DONE]", StringComparison.OrdinalIgnoreCase);
        var text = ClaudeService.StripPointTags(reply.Replace("[DONE]", "", StringComparison.OrdinalIgnoreCase));
        var marks = ClaudeService.ParseMarks(reply).Select(m => m.ToPhysical(shot)).ToList();
        Logger.Log($"[Teach] Step {_steps + 1}{(done ? " (done)" : "")}: \"{text}\" — {marks.Count} mark(s), ≈ ${_spent:F3} so far");
        Remember(request, text);

        if (done || text.Length == 0)
        {
            await Say(text.Length > 0 ? text : L.TeachEnded, ct).ConfigureAwait(false);
            return false;
        }
        _steps++;
        _step = new Step(text, marks, reply.Contains("[UNDO]", StringComparison.OrdinalIgnoreCase));
        _trying = _undoPending = false;
        _checks = 0;
        await PresentAsync(_step, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Draws the step, flies the blue cursor to its first [POINT], says it; watching starts at once (the user may act while it talks).</summary>
    private async Task PresentAsync(Step step, CancellationToken ct, string prefix = "")
    {
        MarksChanged?.Invoke(step.Marks, _slowness);
        if (step.Marks.FirstOrDefault(m => m.Kind == MarkKind.Point) is { } p) point(p.X, p.Y, p.Label);
        Watch();
        await Say(prefix + step.Text, ct).ConfigureAwait(false);
    }

    private async Task WhyAsync(Step step, CancellationToken ct)
    {
        var info = new ReplyInfo();
        var answer = await claude.CompleteAsync(SystemPrompt(), ClaudeService.BuildUserContent(
                $"The user asked why about the current step (\"{step.Text}\"). Explain the reason in one or two short spoken sentences. No tags.", []),
            info, allowEscalation: false, ct, _turns, Config.StepModel).ConfigureAwait(false);
        _spent += info.Cost;
        await Say(ClaudeService.StripPointTags(answer), ct).ConfigureAwait(false);
    }

    /// <summary>Says what and why, then performs the step's tags with the real cursor; the user's own mouse or keys stop it.</summary>
    private async Task ShowMeAsync(Step step, CancellationToken ct)
    {
        if (step.Marks.Count == 0)
        {
            await PresentAsync(step, ct).ConfigureAwait(false);
            return;
        }
        MarksChanged?.Invoke(step.Marks, _slowness);
        await Say(L.TeachWatch + step.Text, ct).ConfigureAwait(false);
        _watching = false;

        var before = ScreenCaptureService.Thumbnail();
        var titleBefore = ForegroundTitle();
        using var demo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var hands = new UserInputWatcher();
        hands.Interrupted += why =>
        {
            Logger.Log($"[Teach] Demo stopped by the user ({why})");
            try { demo.Cancel(); } catch (ObjectDisposedException) { }
        };
        hands.Start();
        hands.Armed = true;
        try
        {
            foreach (var m in step.Marks) await PerformAsync(m, demo.Token).ConfigureAwait(false);
        }
        finally
        {
            hands.Armed = false;
        }
        await Task.Delay(600, ct).ConfigureAwait(false);
        // Only undo what the demo really changed in the document: a view change (orbit, Numpad 0) has no undo step.
        // The thumbnail misses subtle changes (Blender's camera frame), so a new title ("* file") counts too.
        _undoPending = step.Undo && (ScreenCaptureService.Differs(before, ScreenCaptureService.Thumbnail()) ||
                                     ForegroundTitle() != titleBefore);
        Logger.Log($"[Teach] Demo done{(_undoPending ? "; will undo it before the user tries" : "")}");
    }

    private async Task PerformAsync(TeachMark m, CancellationToken ct)
    {
        int Pause(double ms) => (int)(ms * _slowness);
        switch (m.Kind)
        {
            case MarkKind.Point:
                await GlideAsync(m.X, m.Y, ct).ConfigureAwait(false);
                await Task.Delay(Pause(400), ct).ConfigureAwait(false);
                InputDriver.Click(InputDriver.Button.Left, 1, "");
                break;
            case MarkKind.Circle:
                await GlideAsync(m.X, m.Y, ct).ConfigureAwait(false);
                await Task.Delay(Pause(800), ct).ConfigureAwait(false);
                break;
            case MarkKind.Drag:
                await GlideAsync(m.X, m.Y, ct).ConfigureAwait(false);
                await Task.Delay(Pause(300), ct).ConfigureAwait(false);
                await InputDriver.GlideDragAsync((int)m.X, (int)m.Y, (int)m.X2, (int)m.Y2, ButtonOf(m.Button),
                    m.Mods.Replace(',', '+'), Pause(1400), ct).ConfigureAwait(false);
                break;
            case MarkKind.Keys:
                if (m.HasAt) await GlideAsync(m.X, m.Y, ct).ConfigureAwait(false);
                await Task.Delay(Pause(400), ct).ConfigureAwait(false);
                InputDriver.Press(m.Keys.Replace(" ", ""));
                break;
        }
        await Task.Delay(Pause(700), ct).ConfigureAwait(false);
    }

    private Task GlideAsync(double x, double y, CancellationToken ct)
    {
        Win32.GetCursorPos(out var from);
        double distance = Math.Sqrt((x - from.X) * (x - from.X) + (y - from.Y) * (y - from.Y));
        return InputDriver.GlideAsync((int)x, (int)y, (int)(Math.Clamp(300 + distance * 0.6, 350, 1100) * _slowness), ct);
    }

    private static string ForegroundTitle()
    {
        var sb = new StringBuilder(256);
        Win32.GetWindowText(Win32.GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    private static InputDriver.Button ButtonOf(string name) => name.ToLowerInvariant() switch
    {
        var b when b.StartsWith('m') || b == "wheel" => InputDriver.Button.Middle,
        var b when b.StartsWith('r') => InputDriver.Button.Right,
        _ => InputDriver.Button.Left,
    };

    private async Task LetMeTryAsync(Step step, CancellationToken ct)
    {
        if (_undoPending)
        {
            InputDriver.Press("ctrl+z");
            _undoPending = false;
            Logger.Log("[Teach] Undid the demo");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        _trying = true;
        _checks = 0;
        MarksChanged?.Invoke(step.Marks, _slowness);
        Watch(); // after the undo, so its change isn't taken for the user's attempt
        await Say(L.TeachYourTurn, ct).ConfigureAwait(false);
    }

    /// <summary>One cheap look after the screen changed (or "done"): a finished step moves on; a wrong attempt gets a hint.</summary>
    private async Task<bool> CheckAsync(Step step, CancellationToken ct)
    {
        if (OverBudget())
        {
            await Say(L.TeachCap, ct).ConfigureAwait(false);
            return false;
        }
        if (Capture() is not { } shot) return true;
        _checks++;
        var info = new ReplyInfo();
        var reply = (await claude.CompleteAsync(settings.Config.Prompts.TeacherCheckText,
            ClaudeService.BuildUserContent($"The step: {step.Text}", [shot]), info,
            allowEscalation: false, ct, model: Config.CheckModel).ConfigureAwait(false)).Trim();
        _spent += info.Cost;
        Logger.Log($"[Teach] Check {_checks}: \"{reply}\"");

        if (reply.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
        {
            await Say(L.TeachGood, ct).ConfigureAwait(false);
            return await NextStepAsync(ct).ConfigureAwait(false);
        }
        // Not done yet: quiet while the user is just looking around, a hint once they said they're trying
        var hint = reply.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ? reply[2..].TrimStart(':', ',', '.', ' ') : reply;
        if (_trying && hint.Length > 0) await Say(hint, ct).ConfigureAwait(false);
        return true;
    }

    private async Task Say(string text, CancellationToken ct)
    {
        await say(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); // say() swallows the cut-off; the step must stop too
    }

    private void Remember(string request, string reply)
    {
        _turns.Add(new ConversationTurn("user", request));
        _turns.Add(new ConversationTurn("assistant", reply.Length > 0 ? reply : "(no reply)"));
        while (_turns.Count > MaxTurns) _turns.RemoveRange(0, 2);
    }

    private ScreenshotResult? Capture()
    {
        try
        {
            return screen.CaptureAll().FirstOrDefault(s => s.Number == 1);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Teach] Screen capture failed: {ex.Message}");
            return null;
        }
    }

    // ── Screen watcher ──────────────────────────────────────────────────────

    private void Watch()
    {
        if (_checks >= MaxChecksPerStep) return;
        Interlocked.Increment(ref _watchGen);
        _watching = true;
    }

    /// <summary>Thumbnails every 0.7 s while armed; a change that settles posts one Check. Ends an idle lesson.</summary>
    private async Task WatchAsync(CancellationTokenSource lesson)
    {
        var ct = lesson.Token;
        byte[]? last = null;
        int gen = -1;
        try
        {
            while (true)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                bool idle;
                lock (_gate) idle = DateTime.UtcNow - _lastActivity > IdleLimit;
                if (idle)
                {
                    Stop("idle", lesson);
                    return;
                }
                if (!_watching)
                {
                    last = null;
                    continue;
                }
                try
                {
                    if (last == null || gen != _watchGen)
                    {
                        gen = _watchGen;
                        last = ScreenCaptureService.Thumbnail();
                        continue;
                    }
                    var now = ScreenCaptureService.Thumbnail();
                    if (!ScreenCaptureService.Differs(last, now)) continue;
                    for (int i = 0; i < 8; i++) // wait for the screen to settle (the user may still be dragging)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        var next = ScreenCaptureService.Thumbnail();
                        bool settled = !ScreenCaptureService.Differs(now, next);
                        now = next;
                        if (settled) break;
                    }
                    last = now;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue; // locked screen, UAC prompt: try again on the next tick
                }
                if (!_watching || gen != _watchGen) continue;
                _watching = false;
                Post(Command.Check, interrupt: false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Voice commands ──────────────────────────────────────────────────────

    private static readonly (Command Cmd, string[] Phrases)[] Phrases =
    [
        (Command.Next, ["next", "next step", "next one", "go on", "continue", "keep going", "siguiente", "el siguiente", "siguiente paso", "sigue", "continua", "adelante"]),
        (Command.Again, ["again", "repeat", "repeat that", "say that again", "one more time", "what", "otra vez", "repite", "repitelo", "de nuevo"]),
        (Command.ShowMe, ["show me", "show me how", "demo", "do it", "you do it", "muestrame", "ensename", "hazlo", "hazlo tu"]),
        (Command.LetMeTry, ["let me try", "my turn", "i'll try", "let me do it", "dejame intentar", "dejame intentarlo", "dejame probar", "mi turno", "lo intento"]),
        (Command.Slower, ["slower", "slow down", "too fast", "more slowly", "mas despacio", "despacio", "mas lento"]),
        (Command.Why, ["why", "how come", "what for", "por que", "para que"]),
        (Command.Done, ["done", "i did it", "did it", "finished", "i'm done", "listo", "ya esta", "hecho", "lo hice"]),
        (Command.Hush, ["wait", "hold on", "one moment", "espera", "un momento"]),
        (Command.Stop, ["stop", "stop the lesson", "end the lesson", "end lesson", "enough", "that's enough", "para", "basta", "parar", "termina la leccion", "terminar la leccion"]),
    ];

    private static readonly HashSet<string> Fillers = ["clicky", "please", "ok", "okay", "now", "so", "um", "uh", "vale", "ahora", "bueno"];

    /// <summary>
    /// Short turns only (≤ 5 words): the whole turn is a command phrase, or (unless exact) starts with one
    /// ("why do I need that"). A longer sentence ("show me how to render") is a new request, not a command.
    /// </summary>
    public static Command? Parse(string text, bool exact = false)
    {
        var said = Normalize(text).Replace("por favor", " ");
        var words = said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Fillers.Contains(w)).ToArray();
        if (words.Length is 0 or > 5) return null;
        said = string.Join(' ', words);
        foreach (var (cmd, phrases) in Phrases)
            foreach (var p in phrases)
                if (said == p || !exact && said.StartsWith(p + " ", StringComparison.Ordinal))
                    return cmd;
        return null;
    }

    /// <summary>Lowercase, no accents ("Muéstrame" = "muestrame"), letters, digits and apostrophes only.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        }
        return sb.ToString().Trim();
    }
}

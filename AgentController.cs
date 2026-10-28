using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Runs one computer-use task at a time: Allow card (unless "Always allow"), countdown, then the
/// agent with the input watcher armed, so the user's own mouse or keyboard (or Esc) stops it at
/// once. Questions before irreversible steps pause the watcher while the user answers. The result
/// is spoken and shown on the card.
/// </summary>
public sealed class AgentController(AppSettings settings, CompanionManager companion, AgentCard card)
{
    private readonly UserInputWatcher _watcher = new();
    private readonly ClaudeCodeBridge _engineer = new(); // keeps the Claude Code session id for follow-ups
    private int _running;
    private CancellationTokenSource? _cts;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The task being done right now ("" when idle).</summary>
    public string CurrentTask { get; private set; } = "";

    /// <summary>Question and spoken result (for the tray panel).</summary>
    public event Action<string, string>? Finished;

    public void Stop() => _cts?.Cancel();

    /// <summary>Hands-free session: Clicky also says its questions out loud (the user answers by voice).</summary>
    public bool InSession { get; set; }

    /// <param name="preApproved">Hands-free session: starting the session was the approval (no card, no countdown).</param>
    public Task RunAsync(DoTask job, string request, bool preApproved = false) =>
        job.Rank == "engineer"
            ? RunEngineerAsync(job.Task, request, preApproved)
            : RunAsync(job, request, preApproved, null);

    /// <summary>
    /// Engineer rank: the task goes to the user's own Claude Code CLI (code, files, scripts, repos) instead
    /// of the screen agent. Same one-at-a-time rule, Allow card and "stop" as <see cref="RunAsync(DoTask, string, bool)"/>.
    /// </summary>
    /// <param name="folder">Project folder; null picks it from the task ("in ClickyWin") or the workspace root.</param>
    public Task RunEngineerAsync(string task, string request, bool preApproved = false, string? folder = null) =>
        RunAsync(new DoTask("engineer", task), request, preApproved, cts => EngineerAsync(task, folder, cts.Token));

    private async Task RunAsync(DoTask job, string request, bool preApproved,
        Func<CancellationTokenSource, Task<AgentResult>>? work)
    {
        var task = job.Task;
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Logger.Log("[Agent] Already working — ignoring a new task");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        CurrentTask = task;
        try
        {
            if (!settings.AgentAlwaysAllow && !preApproved)
            {
                var choice = await card.AskToStartAsync(task, cts.Token).ConfigureAwait(false);
                Logger.Log($"[Agent] Approval: {choice}");
                if (choice == AgentApproval.NotNow)
                {
                    card.Close(L.AgentNotNow);
                    return;
                }
                if (choice == AgentApproval.AlwaysAllow)
                {
                    settings.AgentAlwaysAllow = true;
                    try { settings.Save(); }
                    catch (Exception ex) { Logger.Log($"[Agent] Could not save Always allow: {ex.Message}"); }
                }
            }

            if (!preApproved && work == null && // the countdown is for letting go of the mouse; Claude Code doesn't need it
                !await card.CountdownAsync(task, settings.Config.Agent.CountdownSeconds, cts.Token).ConfigureAwait(false))
            {
                Logger.Log("[Agent] Cancelled during the countdown");
                card.Close(L.AgentNotNow);
                return;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await (work ?? (c => WorkAsync(job, c)))(cts).ConfigureAwait(false);
            Logger.Log($"[Rank] {job.Rank}{(job.Long ? " long" : "")} → {result.Outcome}, {result.Actions} action(s), " +
                       $"≈ ${result.Cost:F4}, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
            card.Close(result.Summary);
            Finished?.Invoke(request, result.Summary);
            companion.NoteTaskResult($"(Task {result.Outcome}: {result.Summary})"); // so follow-ups know what happened

            // Quiet: a plain success is just "Done"; answers, problems and stops are spoken in full
            bool full = !settings.AgentQuiet || result.Report || result.Outcome != AgentOutcome.Done;
            await companion.SayAsync(full ? result.Summary : L.AgentDone).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Agent] {ex}");
            card.Close(L.AgentFailed);
        }
        finally
        {
            _cts = null;
            CurrentTask = "";
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<AgentResult> EngineerAsync(string task, string? folder, CancellationToken ct)
    {
        folder ??= ClaudeCodeBridge.FolderFor(task);
        string hint = L.EngineerWorkingHint;
        card.ShowWorking(task, "", hint);
        _ = companion.SayAsync(L.EngineerStarting(Path.GetFileName(folder)));
        void OnProgress(string step) => card.ShowWorking(task, step, hint);
        void OnMilestone(string text) => _ = companion.SayAsync(text);
        _engineer.Progress += OnProgress;
        _engineer.Milestone += OnMilestone;
        try
        {
            var r = await Task.Run(() => _engineer.RunAsync(task, folder, ct)).ConfigureAwait(false);
            var outcome = ct.IsCancellationRequested ? AgentOutcome.Stopped : r.Ok ? AgentOutcome.Done : AgentOutcome.Failed;
            Logger.Log($"[Engineer] Result: {(r.Text.Length > 200 ? r.Text[..200] + "…" : r.Text)}");
            return new AgentResult(outcome, ClaudeCodeBridge.Spoken(r.Text), r.Cost, 0, Report: true);
        }
        finally
        {
            _engineer.Progress -= OnProgress;
            _engineer.Milestone -= OnMilestone;
        }
    }

    private async Task<AgentResult> WorkAsync(DoTask job, CancellationTokenSource cts)
    {
        var task = job.Task;
        var agent = new ComputerAgent(settings);
        agent.Progress += step => card.ShowWorking(task, step);
        agent.AskUser = async (question, options, ct) =>
        {
            _watcher.Armed = false; // the user needs their hands to answer
            if (InSession) _ = companion.SayAsync(question); // they may not be looking: ask out loud too
            try { return await card.AskUserAsync(question, options, ct).ConfigureAwait(false); }
            finally
            {
                card.ShowWorking(task, "");
                companion.SetAgentState(AppState.Speaking); // a spoken question ends in Idle: show the cursor again
                _watcher.Armed = true;
            }
        };

        void OnInterrupted(string reason)
        {
            Logger.Log($"[Agent] Stopped by the user ({reason})");
            cts.Cancel();
        }

        _watcher.Interrupted += OnInterrupted;
        _watcher.Start();
        companion.SetAgentState(AppState.Speaking); // the blue triangle follows the pointer while Clicky works
        card.ShowWorking(task, L.AgentStep("screenshot"));
        _watcher.Armed = true;
        try
        {
            return await Task.Run(() => RouteAsync(job, agent, cts.Token)).ConfigureAwait(false);
        }
        finally
        {
            _watcher.Armed = false;
            _watcher.Interrupted -= OnInterrupted;
            _watcher.Stop();
            companion.SetAgentState(AppState.Idle);
        }
    }

    /// <summary>The cheapest worker that can do the job; every rank falls back to the worker.</summary>
    private async Task<AgentResult> RouteAsync(DoTask job, ComputerAgent agent, CancellationToken ct)
    {
        switch (job.Rank)
        {
            case "runner":
                if (Runner.TryStart(job.Task, out var followUp))
                {
                    if (followUp == null) return new AgentResult(AgentOutcome.Done, L.AgentDone, 0, 1);
                    await Task.Delay(2500, ct).ConfigureAwait(false); // let the page load before the first screenshot
                    return await agent.RunAsync(followUp, ct, model: settings.EffectivePrimaryModel).ConfigureAwait(false);
                }
                Logger.Log("[Rank] runner: no shortcut fits → worker");
                break;
            case "teacher":
                if (await TeachAsync(job, ct).ConfigureAwait(false) is { } taught) return taught;
                break;
        }
        return await agent.RunAsync(job.Task, ct, planFirst: job.Long).ConfigureAwait(false);
    }

    /// <summary>Teacher rank hook (P1 Teacher mode plugs in here). Null = not handled, the worker does the task.</summary>
    private static Task<AgentResult?> TeachAsync(DoTask job, CancellationToken ct)
    {
        Logger.Log("[Rank] teacher: not built yet → worker");
        return Task.FromResult<AgentResult?>(null);
    }
}

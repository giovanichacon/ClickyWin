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
    private int _running;
    private CancellationTokenSource? _cts;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Question and spoken result (for the tray panel).</summary>
    public event Action<string, string>? Finished;

    public void Stop() => _cts?.Cancel();

    public async Task RunAsync(string task, string request)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Logger.Log("[Agent] Already working — ignoring a new task");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            if (!settings.AgentAlwaysAllow)
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

            if (!await card.CountdownAsync(task, settings.Config.Agent.CountdownSeconds, cts.Token).ConfigureAwait(false))
            {
                Logger.Log("[Agent] Cancelled during the countdown");
                card.Close(L.AgentNotNow);
                return;
            }

            var result = await WorkAsync(task, cts).ConfigureAwait(false);
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
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<AgentResult> WorkAsync(string task, CancellationTokenSource cts)
    {
        var agent = new ComputerAgent(settings);
        agent.Progress += step => card.ShowWorking(task, step);
        agent.AskUser = async (question, options, ct) =>
        {
            _watcher.Armed = false; // the user needs their hands to answer
            try { return await card.AskUserAsync(question, options, ct).ConfigureAwait(false); }
            finally
            {
                card.ShowWorking(task, "");
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
            return await Task.Run(() => agent.RunAsync(task, cts.Token)).ConfigureAwait(false);
        }
        finally
        {
            _watcher.Armed = false;
            _watcher.Interrupted -= OnInterrupted;
            _watcher.Stop();
            companion.SetAgentState(AppState.Idle);
        }
    }
}

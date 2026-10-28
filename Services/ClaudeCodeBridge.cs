using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Engineer rank: hands a coding/file task to the user's own installed Claude Code CLI
/// (`claude -p … --output-format stream-json`), signed in with their own plan. Clicky never reads,
/// passes or stores Claude credentials; it only starts the process and reads its JSON stream.
/// A follow-up in the same folder within <see cref="ResumeWindow"/> continues the conversation (--resume).
/// </summary>
public sealed class ClaudeCodeBridge
{
    /// <summary>Folder names said in a task ("in ClickyWin") are looked up under this root.</summary>
    public static readonly string WorkspaceRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Claude Code");

    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(30);

    // Edits inside the folder are accepted (acceptEdits); in -p mode anything not listed here is denied, not asked.
    // Later: a voice yes/no via --permission-prompt-tool (needs a small local MCP server in Clicky).
    private static readonly string[] AllowedTools =
    [
        "Read", "Glob", "Grep", "Edit", "Write", "TodoWrite",
        "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
        "Bash(dotnet build:*)", "Bash(ls:*)",
    ];

    private string? _sessionId;
    private string? _sessionFolder;
    private DateTime _lastRun;

    /// <summary>Short status for the card ("Reading App.xaml.cs…").</summary>
    public event Action<string>? Progress;

    /// <summary>Worth saying out loud: the first edit, a build. Fires at most once per kind.</summary>
    public event Action<string>? Milestone;

    public sealed record Result(bool Ok, string Text, string Folder, double Cost);

    /// <summary>The project folder a task names ("in ClickyWin" → …\Claude Code\ClickyWin), else the workspace root.</summary>
    public static string FolderFor(string task)
    {
        string said = Squash(task);
        try
        {
            return Directory.GetDirectories(WorkspaceRoot)
                .Select(d => (dir: d, key: Squash(Path.GetFileName(d))))
                .Where(x => x.key.Length >= 3 && said.Contains(x.key))
                .OrderByDescending(x => x.key.Length) // "ClickyWin-p3" beats "ClickyWin" when both are said
                .Select(x => x.dir)
                .FirstOrDefault() ?? WorkspaceRoot;
        }
        catch (IOException) { return WorkspaceRoot; }
        catch (UnauthorizedAccessException) { return WorkspaceRoot; }
    }

    /// <summary>claude.exe on the user's PATH (the registry copy: apps started at login may have an older one), else the native installer's default.</summary>
    // shortcut: npm installs (claude.cmd) are not found; upgrade if a user has only that
    private static string? FindCli()
    {
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) + ";" +
                   Environment.GetEnvironmentVariable("PATH");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Select(d => Path.Combine(Environment.ExpandEnvironmentVariables(d), "claude.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static string Squash(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    public async Task<Result> RunAsync(string task, string? folder, CancellationToken ct)
    {
        folder ??= FolderFor(task);
        bool resume = _sessionId != null && _sessionFolder == folder && DateTime.UtcNow - _lastRun < ResumeWindow;

        if (!Directory.Exists(folder))
        {
            Logger.Log($"[Engineer] Folder not found: {folder}");
            return new Result(false, L.AgentFailed, folder, 0);
        }
        var exe = FindCli();
        if (exe == null)
        {
            Logger.Log("[Engineer] Claude Code CLI (claude.exe) not found");
            return new Result(false, L.EngineerNoCli, folder, 0);
        }
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", task, "--output-format", "stream-json", "--verbose", "--permission-mode", "acceptEdits", "--allowedTools" })
            psi.ArgumentList.Add(a);
        foreach (var t in AllowedTools) psi.ArgumentList.Add(t);
        if (resume) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(_sessionId!); }
        // The CLI uses its own plan login: drop any API key or host-session auth Clicky inherited (e.g. started from Claude Desktop)
        foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
                                                              k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(name);

        Logger.Log($"[Engineer] claude -p in {folder}{(resume ? " (resume)" : "")}");
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();
        _ = proc.StandardError.ReadToEndAsync(); // drain; never logged (may echo environment details)
        using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        var said = new HashSet<string>();
        string? final = null;
        bool ok = false;
        double cost = 0;
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                    _sessionId = sid.GetString();
                switch (root.GetProperty("type").GetString())
                {
                    case "assistant":
                        foreach (var item in root.GetProperty("message").GetProperty("content").EnumerateArray())
                            OnContent(item, said);
                        break;
                    case "result":
                        ok = root.TryGetProperty("is_error", out var err) && !err.GetBoolean();
                        final = root.TryGetProperty("result", out var r) ? r.GetString() : null;
                        cost = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDouble() : 0;
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        _sessionFolder = folder;
        _lastRun = DateTime.UtcNow;

        if (ct.IsCancellationRequested) return new Result(false, L.AgentStopped, folder, cost);
        Logger.Log($"[Engineer] Finished ok={ok} exit={proc.ExitCode} cost≈${cost:0.000} (plan usage, estimate)");
        if (!ok && final?.Contains("authenticate", StringComparison.OrdinalIgnoreCase) == true)
            return new Result(false, L.EngineerLogin, folder, cost);
        return new Result(ok, string.IsNullOrWhiteSpace(final) ? L.AgentFailed : final.Trim(), folder, cost);
    }

    private void OnContent(JsonElement item, HashSet<string> said)
    {
        switch (item.GetProperty("type").GetString())
        {
            case "text":
                var text = item.GetProperty("text").GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) Progress?.Invoke(text.Length > 160 ? text[..160] + "…" : text);
                break;
            case "tool_use":
                string tool = item.GetProperty("name").GetString() ?? "";
                var input = item.GetProperty("input");
                string file = input.TryGetProperty("file_path", out var f) ? Path.GetFileName(f.GetString() ?? "") : "";
                string cmd = input.TryGetProperty("command", out var k) ? k.GetString() ?? "" : "";
                Logger.Log($"[Engineer] {tool} {file}"); // tool and file name only, no contents
                Progress?.Invoke(L.EngineerStep(tool, file, cmd));
                if ((tool is "Edit" or "Write") && said.Add("edit")) Milestone?.Invoke(L.EngineerEditing(file));
                if (cmd.StartsWith("dotnet build") && said.Add("build")) Milestone?.Invoke(L.EngineerBuilding);
                break;
        }
    }

    /// <summary>The first sentence or two of a (markdown) result, for speaking.</summary>
    public static string Spoken(string text)
    {
        var plain = Regex.Replace(text, @"```.*?```", " ", RegexOptions.Singleline);
        plain = Regex.Replace(plain, @"[`*#>_|]|^\s*[-•]\s*", " ", RegexOptions.Multiline);
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        var split = new SentenceSplitter();
        var sentences = split.Add(plain).Concat(split.Flush()).Take(2);
        var spoken = string.Join(" ", sentences);
        return spoken.Length > 260 ? spoken[..260].TrimEnd() + "…" : spoken;
    }
}

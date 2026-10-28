using System.Diagnostics;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Runner rank: mundane jobs with zero agent tokens. Understands "youtube &lt;search&gt;" and
/// "open &lt;app or website&gt;" (the receptionist writes them that way); anything else goes to the worker.
/// </summary>
public static partial class Runner
{
    // Spoken name → what Process.Start opens (exe on PATH or a URI); shortcut: fixed list, add apps as users ask for them
    private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "notepad.exe", ["bloc de notas"] = "notepad.exe",
        ["calculator"] = "calc.exe", ["calculadora"] = "calc.exe",
        ["paint"] = "mspaint.exe",
        ["explorer"] = "explorer.exe", ["file explorer"] = "explorer.exe", ["explorador"] = "explorer.exe",
        ["settings"] = "ms-settings:", ["configuración"] = "ms-settings:", ["configuracion"] = "ms-settings:",
        ["chrome"] = "chrome.exe", ["google chrome"] = "chrome.exe",
        ["edge"] = "msedge.exe", ["microsoft edge"] = "msedge.exe",
        ["spotify"] = "spotify:",
        ["task manager"] = "taskmgr.exe", ["administrador de tareas"] = "taskmgr.exe",
        ["youtube"] = "https://www.youtube.com",
    };

    [GeneratedRegex(@"^(?:youtube|yt)\s*:?\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeCommand();

    [GeneratedRegex(@"^(?:open|abrir|abre)\s+(?:the\s+|el\s+|la\s+)?(.+?)(?:\s+app)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenCommand();

    // A bare domain or http(s) URL, no spaces ("gmail.com", "https://x.com/home")
    [GeneratedRegex(@"^(?:https?://)?[\w-]+(?:\.[\w-]+)+(?:/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Website();

    /// <summary>
    /// Starts the shortcut for <paramref name="command"/>. False = no shortcut fits (use the worker).
    /// <paramref name="followUp"/> is a small task left for a cheap agent run (click the first video), or null.
    /// </summary>
    public static bool TryStart(string command, out string? followUp)
    {
        followUp = null;
        var text = command.Trim().TrimEnd('.');
        string? target = null;
        if (YouTubeCommand().Match(text) is { Success: true } yt)
        {
            var query = yt.Groups[1].Value.Trim().Trim('"');
            target = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
            followUp = $"The YouTube search results for \"{query}\" are open (or still loading) in the browser. " +
                       "Click the first real video result (skip ads, Shorts, channels and playlists) so it plays, then stop.";
        }
        else if (OpenCommand().Match(text) is { Success: true } open)
        {
            var name = open.Groups[1].Value.Trim().Trim('"');
            if (Apps.TryGetValue(name, out var app)) target = app;
            else if (Website().IsMatch(name)) target = name.Contains("://") ? name : "https://" + name;
        }
        if (target == null) return false;

        try
        {
            // Only fixed apps and http(s) addresses reach the shell, never free text
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            Logger.Log($"[Runner] Opened {target}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Runner] Could not open {target}: {ex.Message}");
            followUp = null;
            return false;
        }
    }
}

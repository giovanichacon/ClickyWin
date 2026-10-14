using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ClickyWindows.Helpers;

/// <summary>Where text will land: the foreground app and, when UI Automation exposes it, the focused box.</summary>
public sealed record FocusContext(
    IntPtr Window, string App, string Title, string FieldText, string Selection, AutomationElement? Element)
{
    public string Describe() => string.IsNullOrEmpty(Title) ? App : $"{App} — {Title}";
}

/// <summary>
/// Reads the focused text box through UI Automation (TextPattern / ValuePattern): exact text and
/// selection, cheaper and more reliable than reading pixels. Password boxes are never read.
/// UIA calls can hang on a frozen app, so every read runs with a timeout off the UI thread.
/// </summary>
public static class FocusReader
{
    private const int MaxFieldChars = 6000;

    public static async Task<FocusContext> CaptureAsync(bool readText, TimeSpan timeout)
    {
        var hwnd = Win32.GetForegroundWindow();
        var (app, title) = WindowInfo(hwnd);
        if (!readText) return new FocusContext(hwnd, app, title, "", "", null);

        try
        {
            return await Task.Run(() =>
            {
                var element = AutomationElement.FocusedElement;
                var (text, selection) = ReadText(element);
                return new FocusContext(hwnd, app, title, text, selection, element);
            }).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Focus] Could not read the focused box in {app}: {ex.GetType().Name}");
            return new FocusContext(hwnd, app, title, "", "", null);
        }
    }

    /// <summary>Current text of an element read earlier (used to learn corrected words).</summary>
    public static async Task<string?> ReadTextAsync(AutomationElement element, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => ReadText(element).text).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch
        {
            return null; // element gone, app closed or frozen
        }
    }

    private static (string text, string selection) ReadText(AutomationElement? element)
    {
        if (element == null || element.Current.IsPassword) return ("", "");

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern text)
        {
            var selection = new StringBuilder();
            foreach (var range in text.GetSelection())
                selection.Append(range.GetText(MaxFieldChars));
            return (text.DocumentRange.GetText(MaxFieldChars), selection.ToString());
        }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern value)
        {
            var v = value.Current.Value ?? "";
            return (v.Length > MaxFieldChars ? v[..MaxFieldChars] : v, "");
        }
        return ("", "");
    }

    private static (string app, string title) WindowInfo(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        string app = "";
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            app = process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description
                ? description
                : process.ProcessName;
        }
        catch
        {
            // Elevated or protected process: the title is still useful
        }
        return (app, sb.ToString());
    }
}

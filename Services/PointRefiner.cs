using System.Drawing;
using System.Windows.Automation;
using ClickyWindows.Helpers;
using ClickyWindows.Models;

namespace ClickyWindows.Services;

/// <summary>
/// Makes a [POINT] land on the element. Screenshots are downscaled (2560 px wide → ~1430 px), so a
/// taskbar icon is ~13 px and the model's guess can miss. Two steps, cheapest first:
/// 1. UI Automation: an element in the taskbar or the foreground window whose name matches the
///    label gives its exact rectangle (taskbar icons, buttons, menus, tabs). No API call.
/// 2. Zoom: a full-resolution crop around the guess goes back to the everyday model, which
///    points again inside the crop.
/// A name match far from the guess is ignored ("Settings" must not jump to an unrelated
/// "Dictation settings" button), except a unique taskbar match. If the zoomed look says the
/// element is not there, the guess is wrong and nothing is pointed at. Errors keep the guess.
/// </summary>
public sealed class PointRefiner(ClaudeService claude, ScreenCaptureService screen)
{
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ZoomTimeout = TimeSpan.FromSeconds(4);
    private const int CropWidth = 1280, CropHeight = 720;
    // A name match farther than this share of the screen diagonal is likely a different thing
    private const double NearDistance = 0.1;

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "icon", "button", "menu", "tab", "link", "field", "box", "app", "option",
        "el", "la", "los", "las", "un", "una", "de", "del", "icono", "botón", "boton", "menú", "pestaña",
        "enlace", "campo", "opción", "opcion",
    };

    private static readonly Condition Candidates = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new NotCondition(new PropertyCondition(AutomationElement.NameProperty, "")),
        new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)));

    /// <param name="description">The reply's text, which often describes the element ("next to Premiere").</param>
    /// <returns>Where to point, or null when the element is not where the model thought.</returns>
    public async Task<(double x, double y)?> RefineAsync(double x, double y, string label, string description,
        Rectangle screenBounds, CancellationToken ct)
    {
        try
        {
            if (await FindByNameAsync(label, x, y, screenBounds).ConfigureAwait(false) is { } hit)
            {
                Logger.Log($"[Point] UI Automation found \"{hit.name}\" → ({hit.x:F0},{hit.y:F0})");
                return (hit.x, hit.y);
            }
            ct.ThrowIfCancellationRequested();
            var (zoom, absent) = await ZoomAsync(x, y, label, description, screenBounds, ct).ConfigureAwait(false);
            if (zoom is { } z)
            {
                Logger.Log($"[Point] Zoom refined → ({z.x:F0},{z.y:F0})");
                return z;
            }
            if (absent)
            {
                Logger.Log($"[Point] \"{label}\" is not near the guess — not pointing");
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Point] Refine failed ({ex.GetType().Name}: {ex.Message}) — keeping the guess");
        }
        return (x, y);
    }

    // ── 1. UI Automation ────────────────────────────────────────────────────

    private static async Task<(double x, double y, string name)?> FindByNameAsync(string label, double x, double y,
        Rectangle screenBounds)
    {
        var words = Words(label);
        if (words.Count == 0) return null;
        try
        {
            return await Task.Run(() => Search(words, x, y, screenBounds)).WaitAsync(UiaTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Log("[Point] UI Automation search timed out");
            return null;
        }
    }

    internal static (double x, double y, string name)? Search(List<string> words, double x, double y, Rectangle screenBounds)
    {
        double near = NearDistance * Math.Sqrt((double)screenBounds.Width * screenBounds.Width +
                                               (double)screenBounds.Height * screenBounds.Height);
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);

        (double x, double y, string name)? best = null;
        double bestDistance = double.MaxValue;
        bool bestInTaskbar = false;
        int matches = 0;
        var roots = Roots(x, y).ToList(); // live elements: fetched before the "cached only" mode applies
        using (cache.Activate())
        {
            foreach (var (root, isTaskbar) in roots)
            {
                AutomationElementCollection found;
                try { found = root.FindAll(TreeScope.Descendants, Candidates); }
                catch (ElementNotAvailableException) { continue; }

                foreach (AutomationElement element in found)
                {
                    var name = element.Cached.Name;
                    var rect = element.Cached.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 2 || !Matches(words, name)) continue;
                    double cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2;
                    if (!screenBounds.Contains((int)cx, (int)cy)) continue;
                    double distance = Math.Sqrt((cx - x) * (cx - x) + (cy - y) * (cy - y));
                    matches++;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (cx, cy, name);
                        bestInTaskbar = isTaskbar;
                    }
                }
            }
        }
        if (best is { } b && bestDistance > near && !(bestInTaskbar && matches == 1))
        {
            Logger.Log($"[Point] Ignored far name match \"{b.name}\" ({bestDistance:F0} px from the guess)");
            return null;
        }
        return best;
    }

    /// <summary>The taskbar, the foreground window and the window under the guess (e.g. the desktop's icons).</summary>
    private static IEnumerable<(AutomationElement root, bool isTaskbar)> Roots(double x, double y)
    {
        var taskbar = Win32.FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero) yield return (AutomationElement.FromHandle(taskbar), true);

        var foreground = Win32.GetForegroundWindow();
        var under = Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT { X = (int)x, Y = (int)y }), Win32.GA_ROOT);
        foreach (var hwnd in under == foreground ? [foreground] : new[] { foreground, under })
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (hwnd != IntPtr.Zero && hwnd != taskbar && pid != Environment.ProcessId)
                yield return (AutomationElement.FromHandle(hwnd), false);
        }
    }

    internal static List<string> Words(string label) =>
        label.Split([' ', '-', '_', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !GenericWords.Contains(w))
            .ToList();

    /// <summary>Every meaningful label word appears in the element name ("Lightroom" ⊂ "Adobe Lightroom Classic - 1 running window").</summary>
    internal static bool Matches(List<string> words, string name) =>
        name.Length > 0 && words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ── 2. Zoom ─────────────────────────────────────────────────────────────

    /// <returns>The refined point, or absent = true when the model says the element is not in the crop.</returns>
    internal async Task<((double x, double y)? point, bool absent)> ZoomAsync(double x, double y, string label, string description,
        Rectangle screenBounds, CancellationToken ct)
    {
        int width = Math.Min(CropWidth, screenBounds.Width), height = Math.Min(CropHeight, screenBounds.Height);
        int left = Math.Clamp((int)x - width / 2, screenBounds.Left, screenBounds.Right - width);
        int top = Math.Clamp((int)y - height / 2, screenBounds.Top, screenBounds.Bottom - height);
        var crop = screen.CaptureRegion(new Rectangle(left, top, width, height));

        var system =
            "You find one element in a full-resolution crop of the user's screen.\n" +
            "Reply with only [POINT:x,y:label] at the element's center in this image's pixel coordinates, " +
            "or exactly [NONE] if it is not visible in the image.";
        var request = $"Element: \"{label}\".\nDescription: {description}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ZoomTimeout);
        string reply;
        try
        {
            reply = await claude.CompleteAsync(system, ClaudeService.BuildUserContent(request, [crop]),
                new ReplyInfo(), allowEscalation: false, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Log("[Point] Zoom timed out — keeping the guess");
            return (null, false);
        }

        if (ClaudeService.ParsePoints(reply) is not [var point, ..])
        {
            bool absent = reply.Contains("[NONE]", StringComparison.OrdinalIgnoreCase);
            if (!absent) Logger.Log($"[Point] Zoom: \"{reply.Trim()}\" — keeping the guess");
            return (null, absent);
        }
        return (crop.ToPhysical(point.X, point.Y), false);
    }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClickyWindows.Helpers;

namespace ClickyWindows;

public enum AgentApproval { NotNow, AllowOnce, AlwaysAllow }

/// <summary>
/// The computer-use card. Asking modes take focus (Enter = first button, Esc = No / Not now) and
/// give the focus back to the window the user was in when they close; the working banner is
/// click-through and never takes focus. Hidden from screenshots. Methods may be called from any
/// thread.
/// </summary>
public partial class AgentCard : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    private TaskCompletionSource<int>? _choice; // 0 = first button, 1 = second, 2 = third, -1 = dismissed
    private IntPtr _hwnd, _previousForeground;
    private int _mode; // bumped by every ShowMode, so a delayed hide can tell it was superseded
    private string? _idleTitle, _idleHint; // hands-free session banner, shown whenever nothing else is
    private volatile string?[] _askLabels = [];
    private volatile bool _askYesNo;

    public AgentCard()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
        PreviewKeyDown += OnKey;
    }

    // ── Public API (any thread) ─────────────────────────────────────────────

    /// <summary>Asks to start a task. Times out as "Not now" after a minute.</summary>
    public async Task<AgentApproval> AskToStartAsync(string task, CancellationToken ct)
    {
        int choice = await AskAsync(L.AgentCardTitle, task, "", L.AgentAllowOnce, L.AgentAlwaysAllow, L.AgentNotNowButton, ct)
            .ConfigureAwait(false);
        return choice switch { 0 => AgentApproval.AllowOnce, 1 => AgentApproval.AlwaysAllow, _ => AgentApproval.NotNow };
    }

    /// <summary>
    /// Clicky's question: Yes/No before an irreversible step (returns "yes" or null), or a choice
    /// between 2–3 options such as Chrome profiles (returns the chosen option, or null).
    /// </summary>
    public async Task<string?> AskUserAsync(string question, string[]? options, CancellationToken ct)
    {
        if (options == null)
            return await AskAsync(L.AgentAskTitle, question, "", L.AgentYes, L.AgentNo, null, ct).ConfigureAwait(false) == 0
                ? "yes" : null;
        int choice = await AskAsync(L.AgentAskTitle, question, "", options[0], options[1],
            options.Length > 2 ? options[2] : null, ct).ConfigureAwait(false);
        return choice >= 0 && choice < options.Length ? options[choice] : null;
    }

    /// <summary>Counts down before the first action. False when the user cancelled (Esc or the button).</summary>
    public async Task<bool> CountdownAsync(string task, int seconds, CancellationToken ct)
    {
        var choice = Begin(ct);
        await Dispatcher.InvokeAsync(() => ShowMode(L.AgentCardTitle, task, L.AgentStarting(seconds),
            null, L.AgentNotNowButton, null, interactive: true));
        for (int s = seconds; s > 0; s--)
        {
            int left = s;
            await Dispatcher.InvokeAsync(() => HintText.Text = L.AgentStarting(left));
            if (await Task.WhenAny(choice, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false) == choice)
                break;
        }
        bool go = !choice.IsCompleted && !ct.IsCancellationRequested;
        _choice?.TrySetResult(-1);
        await Dispatcher.InvokeAsync(RestoreFocus);
        return go;
    }

    /// <summary>The click-through banner while Clicky works.</summary>
    public void ShowWorking(string task, string step) => Dispatcher.InvokeAsync(() =>
        ShowMode(task, step, L.AgentWorkingHint, null, null, null, interactive: false));

    public void Close(string? finalMessage = null) => Dispatcher.InvokeAsync(async () =>
    {
        if (finalMessage != null)
        {
            ShowMode(finalMessage, "", "", null, null, null, interactive: false);
            int mode = _mode;
            await Task.Delay(2500);
            if (mode != _mode) return;
        }
        if (_idleTitle != null) ShowMode(_idleTitle, "", _idleHint ?? "", null, null, null, interactive: false);
        else Hide();
    });

    /// <summary>True while a question (Yes/No or a choice) waits for an answer.</summary>
    public bool IsAsking => _choice is { Task.IsCompleted: false };

    /// <summary>The hands-free session banner (null hides it); shown whenever no question or task is.</summary>
    public void SetIdleBanner(string? title, string? hint) => Dispatcher.InvokeAsync(() =>
    {
        _idleTitle = title;
        _idleHint = hint;
        if (title != null) ShowMode(title, "", hint ?? "", null, null, null, interactive: false);
        else Hide();
    });

    /// <summary>
    /// Answers the open question by voice: yes/no words, an option's name, or "first/second/third".
    /// False when nothing is being asked or the words don't match a button.
    /// </summary>
    public bool TryAnswer(string spoken)
    {
        var choice = _choice;
        if (choice == null || choice.Task.IsCompleted) return false;
        string text = Normalize(spoken);
        int index = -2;
        if (_askYesNo)
        {
            if (YesWords.Any(w => StartsWithWord(text, w))) index = 0;
            else if (NoWords.Any(w => StartsWithWord(text, w))) index = 1;
        }
        else
        {
            var labels = _askLabels;
            for (int i = 0; i < labels.Length && index < 0; i++)
                if (labels[i] is { } label && Normalize(label) is { Length: > 2 } l && (text.Contains(l) || l.Contains(text)))
                    index = i;
            if (index < 0)
                for (int i = 0; i < Ordinals.Length && index < 0; i++)
                    if (Ordinals[i].Any(w => StartsWithWord(text, w) || text.Contains(" " + w + " "))) index = i;
            if (index < 0 && NoWords.Any(w => StartsWithWord(text, w))) index = -1;
        }
        if (index == -2) return false;
        return choice.TrySetResult(index);
    }

    private static readonly string[] YesWords = ["yes", "yeah", "yep", "sure", "ok", "okay", "go ahead", "do it", "si", "sí", "dale", "claro", "hazlo", "adelante", "vale"];
    private static readonly string[] NoWords = ["no", "nope", "nah", "don't", "dont", "cancel", "cancela", "mejor no"];
    private static readonly string[][] Ordinals =
    [
        ["first", "the first", "one", "primero", "primera", "el primero", "la primera", "uno"],
        ["second", "the second", "two", "segundo", "segunda", "el segundo", "la segunda", "dos"],
        ["third", "the third", "three", "tercero", "tercera", "el tercero", "la tercera", "tres"],
    ];

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray())
            .Replace("  ", " ").Trim();

    private static bool StartsWithWord(string text, string word) =>
        text == word || text.StartsWith(word + " ", StringComparison.Ordinal);

    // ── Modes ───────────────────────────────────────────────────────────────

    private async Task<int> AskAsync(string title, string body, string hint, string first, string second, string? third,
        CancellationToken ct)
    {
        _askYesNo = first == L.AgentYes;
        _askLabels = [first, second, third];
        var choice = Begin(ct);
        await Dispatcher.InvokeAsync(() => ShowMode(title, body, hint, first, second, third, interactive: true));
        var winner = await Task.WhenAny(choice, Task.Delay(TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(false);
        int result = winner == choice ? choice.Result : -1;
        _choice?.TrySetResult(-1);
        await Dispatcher.InvokeAsync(RestoreFocus);
        return result;
    }

    private Task<int> Begin(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _choice, tcs)?.TrySetResult(-1);
        ct.Register(() => tcs.TrySetResult(-1));
        return tcs.Task;
    }

    private void ShowMode(string title, string body, string hint, string? first, string? second, string? third, bool interactive)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        BodyText.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = hint;
        HintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetButton(FirstButton, first);
        SetButton(SecondButton, second);
        SetButton(ThirdButton, third);
        Buttons.Visibility = (first ?? second ?? third) is null ? Visibility.Collapsed : Visibility.Visible;
        _mode++;

        if (!IsVisible)
        {
            if (interactive) _previousForeground = Win32.GetForegroundWindow();
            ShowActivated = interactive;
            Show();
        }
        SetClickThrough(!interactive);
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 8;
        if (interactive)
        {
            if (_previousForeground == IntPtr.Zero) _previousForeground = Win32.GetForegroundWindow();
            Activate();
            (first != null ? FirstButton : SecondButton).Focus();
        }
    }

    private static void SetButton(System.Windows.Controls.Button button, string? text)
    {
        button.Content = text;
        button.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        int style = Win32.GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
        style = on ? style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : style & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        Win32.SetWindowLong(_hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>Gives the keyboard back to the app the user was in, so Clicky's actions land there.</summary>
    private void RestoreFocus()
    {
        SetClickThrough(true);
        if (_previousForeground != IntPtr.Zero && _previousForeground != _hwnd)
            Win32.SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_choice == null) return;
        if (e.Key == Key.Escape)
        {
            _choice.TrySetResult(FirstButton.Visibility == Visibility.Visible ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FirstButton.Visibility == Visibility.Visible)
        {
            _choice.TrySetResult(0);
            e.Handled = true;
        }
    }

    private void OnFirst(object sender, RoutedEventArgs e) => _choice?.TrySetResult(0);
    private void OnSecond(object sender, RoutedEventArgs e) => _choice?.TrySetResult(1);
    private void OnThird(object sender, RoutedEventArgs e) => _choice?.TrySetResult(2);
}

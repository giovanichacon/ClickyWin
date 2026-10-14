using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Application entry point. Owns the tray icon and quick panel, the keyboard hook, and routes
/// shortcuts: Ask → <see cref="CompanionManager"/>, Dictate/Write → <see cref="DictationManager"/>
/// (one at a time, they share the microphone).
/// </summary>
public partial class App
{
    private NotifyIcon? _trayIcon;
    private QuickPanel? _panel;
    private AudioCaptureService? _mic;
    private CompanionManager? _companion;
    private DictationManager? _dictation;
    private KeyboardHookService? _keys;
    private OverlayWindow? _overlay;
    private MainWindow? _mainWindow;
    private AppSettings _settings = new();
    private SetupWindow? _setupWindow;
    private string _askText = HotkeyParser.Default, _dictateText = "", _writeText = "", _promptText = "";
    private string _lastQuestion = "", _lastAnswer = "";

    // Which shortcut owns the current gesture. Touched only on the hook's event task (one at a time).
    private ShortcutAction? _gestureOwner;

    // Held in a field for the whole process lifetime: a local would be collected by the GC,
    // releasing the mutex and letting a second instance start.
    private static System.Threading.Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard — before touching Logger, so a second launch can't rotate
        // the running instance's log file.
        _singleInstanceMutex = new System.Threading.Mutex(true, "ClickyWindows_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            System.Windows.MessageBox.Show(
                L.AlreadyRunning,
                "Clicky", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        InstallCrashHandlers();

        // Wire Logger to tray balloon tips (async: callers may be on background threads)
        Logger.OnError += msg => Dispatcher.InvokeAsync(() => ShowBalloon(msg, ToolTipIcon.Error));
        Logger.OnInfo  += msg => Dispatcher.InvokeAsync(() => ShowBalloon(msg, ToolTipIcon.Info));

        Logger.Log("=== Clicky starting ===");

        _settings = AppSettings.Load();
        L.Setting = _settings.Language;
        Logger.Log($"Settings: {AppSettings.FilePath} | config: {AppConfig.FilePath}");
        Logger.Log($"Models: {_settings.EffectivePrimaryModel} → {_settings.EffectiveEscalationModel ?? "no escalation"}");

        _mic = new AudioCaptureService { DeviceId = _settings.MicDeviceId };
        if (_settings.Config.MicWarmUp)
            _ = Task.Run(_mic.WarmUpAsync); // first press starts fast (mic indicator blinks once)

        _companion = new CompanionManager(_settings, _mic);
        _dictation = new DictationManager(_settings, _mic, Dispatcher);

        _overlay = new OverlayWindow();
        _overlay.SetAlwaysShowCursor(_settings.AlwaysShowCursor);
        _overlay.Show();

        _mainWindow = new MainWindow(_companion, _dictation, _overlay);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        _panel = new QuickPanel(_settings, OpenSetup);
        _companion.StateChanged += state => Dispatcher.InvokeAsync(() => _panel.SetState(state));
        _dictation.StateChanged += state => Dispatcher.InvokeAsync(() => _panel.SetState(state));
        _companion.AnswerReady += OnAnswerReady;
        _dictation.Inserted += _ => Dispatcher.InvokeAsync(() => _panel.SetDictations(_dictation.Recent));

        _keys = new KeyboardHookService();
        _keys.Started += OnShortcutStarted;
        _keys.Finished += OnShortcutFinished;
        _keys.Cancelled += OnShortcutCancelled;
        _keys.HandsFreeLocked += action =>
        {
            if (_gestureOwner == action && action != ShortcutAction.Ask) _dictation.NotifyHandsFree();
        };
        ApplyShortcuts();
        _keys.Start();

        SetupTrayIcon();

        Logger.Log($"Clicky ready. Ask: {_askText} | Dictate: {_dictateText} | Write: {_writeText} | Prompt: {_promptText}");
        if (e.Args.Contains("--panel"))
            _panel.ShowNearTray();
        else if (_settings.HasRequiredKeys && !e.Args.Contains("--settings"))
            ShowBalloon(L.Ready(_askText), ToolTipIcon.Info);
        else
            OpenSetup(); // first run (keys are needed before anything works) or "--settings"
    }

    // ── Shortcut routing (hook event task) ──────────────────────────────────

    private void ApplyShortcuts()
    {
        var bindings = new List<KeyboardHookService.Binding>();
        _askText = Bind(ShortcutAction.Ask, _settings.EffectiveHotkey, _settings.Config.Hotkey, false, bindings);
        _dictateText = Bind(ShortcutAction.Dictate, _settings.EffectiveDictateHotkey, _settings.Config.DictateHotkey, true, bindings);
        _writeText = Bind(ShortcutAction.Write, _settings.EffectiveWriteHotkey, _settings.Config.WriteHotkey, true, bindings);
        _promptText = Bind(ShortcutAction.Prompt, _settings.EffectivePromptHotkey, _settings.Config.PromptHotkey, true, bindings);
        _keys?.SetBindings(bindings);
        _panel?.SetShortcuts(_askText, _dictateText, _writeText, _promptText);
    }

    private static string Bind(ShortcutAction action, string text, string fallback, bool handsFree,
        List<KeyboardHookService.Binding> into)
    {
        if (text.Equals("Off", StringComparison.OrdinalIgnoreCase)) return L.Off;
        var (chord, shown) = HotkeyParser.ParseOrDefault(text, HotkeyParser.TryParse(fallback, out _) ? fallback : HotkeyParser.Default);
        into.Add(new KeyboardHookService.Binding(action, chord, handsFree));
        return shown;
    }

    private void OnShortcutStarted(ShortcutAction action)
    {
        if (_companion == null || _dictation == null) return;
        if (action == ShortcutAction.Ask)
        {
            if (_dictation.IsBusy) return;
            _gestureOwner = action;
            _companion.OnPushToTalkPressed();
        }
        else
        {
            if (_companion.IsListening || _dictation.IsBusy) return;
            _gestureOwner = action;
            _companion.StopSpeaking();
            _dictation.Start(action switch
            {
                ShortcutAction.Write => DictationMode.Write,
                ShortcutAction.Prompt => DictationMode.Prompt,
                _ => DictationMode.Dictate,
            });
        }
    }

    private void OnShortcutFinished(ShortcutAction action)
    {
        if (_gestureOwner != action) return;
        _gestureOwner = null;
        if (action == ShortcutAction.Ask) _companion?.OnPushToTalkReleased();
        else _dictation?.Finish();
    }

    private void OnShortcutCancelled(ShortcutAction action)
    {
        if (_gestureOwner != action) return;
        _gestureOwner = null;
        if (action == ShortcutAction.Ask) _companion?.OnPushToTalkCancelled();
        else _dictation?.Cancel();
    }

    private void OnAnswerReady(string question, string answer)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _lastQuestion = question;
            _lastAnswer = answer;
            _panel?.SetAnswer(question, answer);
            if (_settings.CopyAnswers) TextInserter.SetClipboardText(answer);
        });
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private void OpenSetup()
    {
        if (_setupWindow != null)
        {
            _setupWindow.Activate();
            return;
        }

        _setupWindow = new SetupWindow(_settings, _keys);
        try
        {
            if (_setupWindow.ShowDialog() == true)
            {
                if (_mic != null) _mic.DeviceId = _settings.MicDeviceId;
                _overlay?.SetAlwaysShowCursor(_settings.AlwaysShowCursor);
                if (!_settings.TutorMode) _companion?.StopTutor();
                ApplyShortcuts();
                _panel?.Localize();
                _panel?.SetAnswer(_lastQuestion, _lastAnswer);
                RebuildTrayMenu();
                Logger.Log($"Models: {_settings.EffectivePrimaryModel} → {_settings.EffectiveEscalationModel ?? "no escalation"}");
                if (_settings.HasRequiredKeys)
                    ShowBalloon(L.Ready(_askText), ToolTipIcon.Info);
            }
        }
        finally
        {
            _setupWindow = null;
        }
    }

    // ── Tray ────────────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(new SolidBrush(System.Drawing.Color.FromArgb(0x25, 0x63, 0xEB)), 1, 1, 14, 14);
            g.DrawEllipse(new Pen(System.Drawing.Color.White, 1.5f), 2, 2, 12, 12);
        }
        var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());

        _trayIcon = new NotifyIcon { Icon = icon, Visible = true };
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button != MouseButtons.Left || _panel == null) return;
            if (_panel.IsVisible) _panel.Hide();
            else
            {
                _panel.SetDictations(_dictation?.Recent ?? []);
                _panel.ShowNearTray();
            }
        };
        RebuildTrayMenu();
    }

    /// <summary>Menu text follows the UI language, so it is rebuilt after Settings are saved.</summary>
    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;
        var old = _trayIcon.ContextMenuStrip;

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Clicky  |  {L.HoldToTalk(_askText)}").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.MenuSettings, null, (_, _) => OpenSetup());
        menu.Items.Add(L.MenuLog, null, (_, _) => OpenLog());
        menu.Items.Add(L.MenuFolder, null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.MenuQuit, null, (_, _) => QuitApp());

        _trayIcon.ContextMenuStrip = menu;
        var tip = $"Clicky — {L.HoldToTalk(_askText)}";
        _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon limit
        old?.Dispose();
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _trayIcon?.ShowBalloonTip(5000, "Clicky", message, icon);
    }

    private void OpenLog()
    {
        try { System.Diagnostics.Process.Start("notepad.exe", Logger.LogFilePath); }
        catch { }
    }

    private void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(Logger.LogFilePath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private async void QuitApp()
    {
        try
        {
            _trayIcon?.Dispose();
            _keys?.Dispose();
            var cleanup = Task.WhenAll(
                _companion?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _dictation?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            _mic?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Quit] Cleanup error: {ex.Message}");
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _keys?.Dispose();
        Logger.Log("=== Clicky exiting ===");
        Logger.Flush(TimeSpan.FromSeconds(1));
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ── Crash handling ──────────────────────────────────────────────────────
    // A tray app has no window to show a crash dialog, so unhandled errors are logged and,
    // where possible, survived: one failed interaction must not kill the background app.

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error($"[Crash] UI thread: {args.Exception}");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log($"[Crash] Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // Background-thread exception: the runtime terminates the process after this
            Logger.Log($"[Crash] Fatal (terminating={args.IsTerminating}): {args.ExceptionObject}");
            Logger.Flush(TimeSpan.FromSeconds(1));
        };
    }
}

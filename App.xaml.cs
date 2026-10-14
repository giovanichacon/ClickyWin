using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ClickyWindows.Helpers;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Application entry point. Owns the system tray icon and coordinates startup.
/// </summary>
public partial class App
{
    private NotifyIcon? _trayIcon;
    private CompanionManager? _companion;
    private OverlayWindow? _overlay;
    private MainWindow? _mainWindow;
    private AppSettings _settings = new();
    private SetupWindow? _setupWindow;
    private string _hotkeyText = HotkeyParser.Default;

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

        var (mods, vk, hotkeyText) = HotkeyParser.ParseOrDefault(_settings.Config.Hotkey);
        _hotkeyText = hotkeyText;

        _companion = new CompanionManager(_settings);
        if (_settings.Config.MicWarmUp)
            _ = _companion.WarmUpMicAsync();

        _overlay = new OverlayWindow();
        _overlay.Show();

        _mainWindow = new MainWindow(mods, vk, _companion, _overlay);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        SetupTrayIcon();

        Logger.Log($"Clicky ready. Hotkey: {_hotkeyText}");
        if (_settings.HasRequiredKeys)
            ShowBalloon(L.Ready(_hotkeyText), ToolTipIcon.Info);
        else
            OpenSetup(); // first run: keys are needed before anything works
    }

    private void OpenSetup()
    {
        if (_setupWindow != null)
        {
            _setupWindow.Activate();
            return;
        }

        _setupWindow = new SetupWindow(_settings);
        try
        {
            if (_setupWindow.ShowDialog() == true)
            {
                _companion?.ApplySettings();
                RebuildTrayMenu();
                Logger.Log($"Models: {_settings.EffectivePrimaryModel} → {_settings.EffectiveEscalationModel ?? "no escalation"}");
                if (_settings.HasRequiredKeys)
                    ShowBalloon(L.Ready(_hotkeyText), ToolTipIcon.Info);
            }
        }
        finally
        {
            _setupWindow = null;
        }
    }

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
        _trayIcon.DoubleClick += (_, _) => OpenSetup();
        RebuildTrayMenu();
    }

    /// <summary>Menu text follows the UI language, so it is rebuilt after Settings are saved.</summary>
    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;
        var old = _trayIcon.ContextMenuStrip;

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Clicky  |  {L.HoldToTalk(_hotkeyText)}").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.MenuSettings, null, (_, _) => OpenSetup());
        menu.Items.Add(L.MenuLog, null, (_, _) => OpenLog());
        menu.Items.Add(L.MenuFolder, null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.MenuQuit, null, (_, _) => QuitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Text = $"Clicky — {L.HoldToTalk(_hotkeyText)}";
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
            if (_companion != null)
                await _companion.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
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

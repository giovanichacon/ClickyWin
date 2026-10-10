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
                "Clicky is already running. Check the system tray.",
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
        Logger.Log($"Settings loaded from: {Logger.LogFilePath.Replace("clicky.log", "settings.json")}");

        if (!ValidateSettings())
        {
            Shutdown();
            return;
        }

        _companion = new CompanionManager(_settings);
        if (_settings.MicWarmUp)
            _ = _companion.WarmUpMicAsync();

        _overlay = new OverlayWindow();
        _overlay.Show();

        _mainWindow = new MainWindow(_settings, _companion, _overlay);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        SetupTrayIcon();

        Logger.Log($"Clicky ready. Hotkey: {GetHotkeyDescription()}");
        Logger.Log($"Log file: {Logger.LogFilePath}");
        ShowBalloon($"Clicky ready! Hold {GetHotkeyDescription()} to talk.", ToolTipIcon.Info);
    }

    private bool ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey) &&
            string.IsNullOrWhiteSpace(_settings.ClaudeProxyUrl))
        {
            _settings.Save();
            var path = Logger.LogFilePath.Replace("clicky.log", "settings.json");
            System.Windows.MessageBox.Show(
                $"Please add your API keys to:\n{path}\n\n" +
                "Required fields:\n• AnthropicApiKey\n• ElevenLabsApiKey\n• AssemblyAiApiKey",
                "Clicky — Setup Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
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

        var hotkeyDesc = GetHotkeyDescription();
        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = $"Clicky — Hold {hotkeyDesc} to talk",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Clicky  |  Hold {hotkeyDesc} to talk").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View Log", null, (_, _) => OpenLog());
        menu.Items.Add("Open Settings", null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Clicky", null, (_, _) => QuitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenLog();
    }

    private string GetHotkeyDescription()
    {
        var parts = new List<string>();
        if ((_settings.HotkeyModifiers & 0x0004) != 0) parts.Add("Shift");
        if ((_settings.HotkeyModifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((_settings.HotkeyModifiers & 0x0001) != 0) parts.Add("Alt");
        if ((_settings.HotkeyModifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(_settings.HotkeyVirtualKey == 0x20 ? "Space" : $"Key(0x{_settings.HotkeyVirtualKey:X})");
        return string.Join("+", parts);
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

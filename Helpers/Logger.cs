using System.Text;
using System.Threading.Channels;

namespace ClickyWindows.Helpers;

/// <summary>
/// File logger. Writes timestamped lines to %APPDATA%\ClickyWindows\clicky.log
/// (the previous run is kept as clicky.prev.log).
///
/// Log() only enqueues — a single background task does the disk I/O — so it is safe and
/// cheap to call from the UI thread, the audio capture thread or the WebSocket loops.
/// Also raises optional callbacks (tray balloon tips) for Info/Error.
/// </summary>
public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClickyWindows");
    private static string LogPath = Path.Combine(LogDir, "clicky.log");

    private static readonly Channel<string> Queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly Task Writer;

    public static Action<string>? OnError;   // hooked by App to show balloon tip
    public static Action<string>? OnInfo;    // hooked by App to show balloon tip

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            for (int attempt = 0; attempt < 3 && File.Exists(LogPath); attempt++)
            {
                try { File.Move(LogPath, Path.Combine(LogDir, "clicky.prev.log"), overwrite: true); }
                catch (IOException) { Thread.Sleep(100); } // briefly locked (antivirus, indexer, editor)
            }
        }
        catch { /* logging must never take the app down */ }

        Writer = Task.Run(WriteLoopAsync);
        Log($"=== Clicky log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    private static FileStream OpenLogFile()
    {
        // ReadWrite sharing so "View Log" can open the file while we hold it
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(150);
            }
            catch (IOException) when (attempt == 3)
            {
                // Still locked: write to a per-process file rather than lose the log
                LogPath = Path.Combine(LogDir, $"clicky-{Environment.ProcessId}.log");
            }
        }
    }

    private static async Task WriteLoopAsync()
    {
        try
        {
            await using var stream = OpenLogFile();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            var reader = Queue.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                    writer.WriteLine(line); // buffered in memory
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Disk full, permissions… leave a trace somewhere visible, never crash
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "clicky-logger-error.txt"), ex.ToString()); }
            catch { }
        }
    }

    public static void Log(string message) =>
        Queue.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public static void Info(string message)
    {
        Log($"[INFO] {message}");
        OnInfo?.Invoke(message);
    }

    public static void Error(string message)
    {
        Log($"[ERROR] {message}");
        OnError?.Invoke(message);
    }

    /// <summary>Stops accepting lines and waits for pending ones to reach disk. Call on exit.</summary>
    public static void Flush(TimeSpan timeout)
    {
        Queue.Writer.TryComplete();
        try { Writer.Wait(timeout); }
        catch { }
    }

    public static string LogFilePath => LogPath;
}

using System.Windows;
using System.Windows.Threading;

namespace ClickyWindows.Helpers;

public enum InsertResult { Typed, Pasted, CopiedOnly }

/// <summary>
/// Puts text where the user's caret is.
/// - Short single-line text is typed as Unicode keystrokes (SendInput + KEYEVENTF_UNICODE):
///   works in Win32, WPF, Chromium and Electron apps and leaves the clipboard alone.
/// - Longer or multi-line text is pasted (typing "\n" would press Enter and send a chat message);
///   the previous clipboard is restored afterwards and the paste is kept out of clipboard history.
/// - If Windows blocks synthetic input (an app running as administrator has focus), the text is
///   left on the clipboard so the user can press Ctrl+V.
/// </summary>
public static class TextInserter
{
    private const int MaxTypedChars = 240;
    private const ushort VK_CONTROL = 0x11, VK_V = 0x56;
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    public static async Task<InsertResult> InsertAsync(string text, Dispatcher ui)
    {
        if (string.IsNullOrEmpty(text)) return InsertResult.Typed;

        // Keys still held from the shortcut would turn letters into shortcuts (Ctrl+A, Win+L…)
        for (int i = 0; i < 75 && ModifierKeys.Any(Win32.IsKeyDown); i++)
            await Task.Delay(20).ConfigureAwait(false);

        bool multiLine = text.Contains('\n');
        if (!multiLine && text.Length <= MaxTypedChars)
        {
            var inputs = new Win32.INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[2 * i] = Win32.Unicode(text[i], up: false);
                inputs[2 * i + 1] = Win32.Unicode(text[i], up: true);
            }
            if (Win32.Send(inputs)) return InsertResult.Typed;
            Logger.Log("[Insert] Typing was blocked — trying paste");
        }

        // Clipboard work must run on an STA thread: the UI thread
        var backup = await ui.InvokeAsync(() => BackupClipboard());
        bool placed = await ui.InvokeAsync(() => SetClipboardText(text, transient: true));
        if (!placed) return InsertResult.CopiedOnly;

        bool pasted = Win32.Send(
        [
            Win32.Key(VK_CONTROL, false), Win32.Key(VK_V, false),
            Win32.Key(VK_V, true), Win32.Key(VK_CONTROL, true),
        ]);
        if (!pasted)
        {
            // Leave the text there (as a normal clipboard entry) for a manual Ctrl+V
            await ui.InvokeAsync(() => SetClipboardText(text, transient: false));
            return InsertResult.CopiedOnly;
        }

        // Give the target app time to read the clipboard before restoring the old content
        await Task.Delay(450).ConfigureAwait(false);
        await ui.InvokeAsync(() =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == text)
                    RestoreClipboard(backup);
            }
            catch (Exception ex)
            {
                Logger.Log($"[Insert] Clipboard restore failed: {ex.Message}");
            }
        });
        return InsertResult.Pasted;
    }

    /// <summary>Puts text on the clipboard (UI thread). Used for "copy answer" and dictation history.</summary>
    public static bool SetClipboardText(string text, bool transient = false)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.UnicodeText, text);
        if (transient)
        {
            // Keep the temporary paste out of Win+V history and cloud clipboard
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(40); // another app has the clipboard open
            }
        }
        Logger.Log("[Insert] Clipboard is busy");
        return false;
    }

    private static Dictionary<string, object>? BackupClipboard()
    {
        try
        {
            var current = System.Windows.Clipboard.GetDataObject();
            if (current == null) return null;
            var saved = new Dictionary<string, object>();
            foreach (var format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    if (current.GetData(format, autoConvert: false) is { } value) saved[format] = value;
                }
                catch { /* some formats can't be read back; skip them */ }
            }
            return saved;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Insert] Clipboard backup failed: {ex.Message}");
            return null;
        }
    }

    private static void RestoreClipboard(Dictionary<string, object>? saved)
    {
        if (saved == null || saved.Count == 0)
        {
            System.Windows.Clipboard.Clear();
            return;
        }
        var data = new System.Windows.DataObject();
        foreach (var (format, value) in saved)
            data.SetData(format, value);
        System.Windows.Clipboard.SetDataObject(data, copy: true);
    }
}

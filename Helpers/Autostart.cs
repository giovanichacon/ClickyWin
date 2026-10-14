using Microsoft.Win32;

namespace ClickyWindows.Helpers;

/// <summary>"Start with Windows": a value under HKCU\...\Run pointing at this exe (no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clicky";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
        Logger.Log($"[Autostart] {(enabled ? "on" : "off")}");
    }
}

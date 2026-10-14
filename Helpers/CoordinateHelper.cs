namespace ClickyWindows.Helpers;

/// <summary>
/// Converts between physical pixels (Win32 / screen capture) and WPF device-independent units.
/// </summary>
internal static class CoordinateHelper
{
    /// <summary>
    /// Gets the DPI scale factor for the primary screen (physical pixels / WPF DIPs).
    /// </summary>
    public static double GetDpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }
}

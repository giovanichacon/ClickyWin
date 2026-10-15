using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClickyWindows.Helpers;

/// <summary>
/// Asks DWM for the mac-panel look on a borderless window: dark frame, rounded corners,
/// a subtle #373B39 border and the system shadow. Windows 11 only; returns false on Windows 10,
/// where the caller keeps its own XAML border.
/// </summary>
internal static class MacChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        int dark = 1, round = DWMWCP_ROUND, border = 0x00393B37; // COLORREF is 0x00BBGGRR → #373B39
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        bool rounded = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
        if (rounded) DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        return rounded;
    }
}

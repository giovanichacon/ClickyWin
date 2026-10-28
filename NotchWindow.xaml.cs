using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ClickyWindows.Helpers;
using ClickyWindows.Models;

namespace ClickyWindows;

/// <summary>
/// Pill at the top center of the primary screen: shows Clicky's state, click opens the panel
/// below it, the gear opens Settings. Never takes focus and is left out of screenshots (so Claude
/// doesn't see it). shortcut: primary monitor only and stays over full-screen apps; per-monitor / hide-on-fullscreen if asked.
/// </summary>
public partial class NotchWindow : Window
{
    private readonly Action _togglePanel;
    private readonly Action _openSettings;

    public NotchWindow(Action togglePanel, Action openSettings)
    {
        InitializeComponent();
        _togglePanel = togglePanel;
        _openSettings = openSettings;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int WS_EX_TOOLWINDOW = 0x80; // no Alt+Tab entry
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
        SizeChanged += (_, _) => Place();
        SetState(AppState.Idle);
    }

    /// <summary>Bottom center of the pill in screen DIPs, where the panel hangs from.</summary>
    public System.Windows.Point Anchor => new(Left + ActualWidth / 2, Top + ActualHeight);

    private void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 0;
    }

    public void SetState(AppState state)
    {
        // same colors as the panel: green when ready, blue while listening, thinking or speaking
        (StatusText.Text, var color) = state switch
        {
            AppState.Listening => (L.PanelListening, Color.FromRgb(0x60, 0xA5, 0xFA)),
            AppState.Processing => (L.PanelThinking, Color.FromRgb(0x60, 0xA5, 0xFA)),
            AppState.Speaking => (L.PanelSpeaking, Color.FromRgb(0x60, 0xA5, 0xFA)),
            _ => ("Clicky", Color.FromRgb(0x34, 0xD3, 0x99)),
        };
        StatusDot.Fill = new SolidColorBrush(color);
        StatusGlow.Color = color;
    }

    private void OnPillClick(object sender, MouseButtonEventArgs e) => _togglePanel();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _openSettings();
    }
}

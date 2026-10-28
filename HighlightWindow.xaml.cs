using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using ClickyWindows.Helpers;

namespace ClickyWindows;

/// <summary>Click-through, capture-excluded edge glow over the primary screen; fades in and out.</summary>
public partial class HighlightWindow : Window
{
    private bool _on;

    public HighlightWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE)
                | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    public void SetColor(Color color)
    {
        Edge.BorderBrush = new SolidColorBrush(color);
        Glow.Color = color;
    }

    public void SetActive(bool on)
    {
        if (on == _on) return;
        _on = on;
        if (on)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Show();
        }
        var fade = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 150 : 250));
        if (!on) fade.Completed += (_, _) => { if (!_on) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }
}

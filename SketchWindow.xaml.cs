using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ClickyWindows.Helpers;
using ClickyWindows.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.Windows.Shapes.Path;

namespace ClickyWindows;

/// <summary>
/// Click-through, capture-excluded drawing board for Teacher mode, shown only while a lesson step has
/// drawings. Marks come in physical screen pixels (the [POINT] mapping) and are drawn on the screen under
/// the cursor. A drag replays three times then stays as a still trace, so the screen watcher sees no motion.
/// </summary>
public partial class SketchWindow : Window
{
    private Brush _accent = new SolidColorBrush(Color.FromRgb(0x33, 0x80, 0xFF));
    private Color _accentColor = Color.FromRgb(0x33, 0x80, 0xFF);
    private System.Drawing.Rectangle _bounds;
    private double _dpi = 1;

    public SketchWindow()
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
        _accentColor = color;
        _accent = new SolidColorBrush(color);
    }

    /// <param name="slowness">1 = normal; "slower" stretches the drag replay.</param>
    public void Draw(IReadOnlyList<TeachMark> marks, double slowness)
    {
        Board.Children.Clear();
        if (marks.All(m => m.Kind == MarkKind.Point)) // points are the blue cursor's job
        {
            Hide();
            return;
        }
        Win32.GetCursorPos(out var cursor);
        _bounds = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
        Show();
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = _bounds.Left / _dpi;
        Top = _bounds.Top / _dpi;
        Width = _bounds.Width / _dpi;
        Height = _bounds.Height / _dpi;

        foreach (var m in marks)
        {
            switch (m.Kind)
            {
                case MarkKind.Circle: DrawCircle(Local(m.X, m.Y), m.R / _dpi); break;
                case MarkKind.Drag: DrawDrag(Local(m.X, m.Y), Local(m.X2, m.Y2), m.Button, m.Mods, slowness); break;
                case MarkKind.Keys: DrawKeys(m.HasAt ? Local(m.X, m.Y) : Local(cursor.X, cursor.Y), m.Keys); break;
            }
        }
    }

    public void Clear()
    {
        Board.Children.Clear();
        Hide();
    }

    private Point Local(double physX, double physY) => new((physX - _bounds.Left) / _dpi, (physY - _bounds.Top) / _dpi);

    private DropShadowEffect Glow(double blur = 14) => new() { Color = _accentColor, BlurRadius = blur, ShadowDepth = 0, Opacity = 0.9 };

    private void DrawCircle(Point c, double r)
    {
        r = Math.Max(r, 18);
        var ring = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = _accent, StrokeThickness = 3, Effect = Glow() };
        Place(ring, c.X - r, c.Y - r);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
            { AutoReverse = true, RepeatBehavior = new RepeatBehavior(4) });
    }

    private void DrawDrag(Point a, Point b, string button, string mods, double slowness)
    {
        // Dashed trace with a start dot and an arrowhead
        Board.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = _accent, StrokeThickness = 3,
            StrokeDashArray = [2, 1.5], StrokeDashCap = PenLineCap.Round, Effect = Glow(10) });
        Place(new Ellipse { Width = 12, Height = 12, Fill = _accent }, a.X - 6, a.Y - 6);
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        Point Tip(double back, double side) => new(b.X - back * Math.Cos(angle) + side * Math.Sin(angle),
                                                   b.Y - back * Math.Sin(angle) - side * Math.Cos(angle));
        Board.Children.Add(new Polygon { Points = [b, Tip(16, 8), Tip(16, -8)], Fill = _accent });

        // Mouse with the held button lit, plus the modifier chip, sliding along the trace
        var icon = MouseIcon(button);
        if (mods.Length > 0)
        {
            var chip = KeyCap(Pretty(mods.Replace(",", "+")) + " +", 12);
            Canvas.SetLeft(chip, -chip.Width - 6);
            Canvas.SetTop(chip, 4);
            icon.Children.Add(chip);
        }
        Board.Children.Add(icon);
        double total = 2.2 * slowness;
        icon.BeginAnimation(Canvas.LeftProperty, Slide(a.X - 11, b.X - 11, total));
        icon.BeginAnimation(Canvas.TopProperty, Slide(a.Y - 16, b.Y - 16, total));
    }

    /// <summary>Hold at the start, glide (eased) to the end, hold; three times, then rest at the end.</summary>
    private static DoubleAnimationUsingKeyFrames Slide(double from, double to, double seconds) => new()
    {
        RepeatBehavior = new RepeatBehavior(3),
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * 0.2))),
            new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * 0.8)), new CubicEase { EasingMode = EasingMode.EaseInOut }),
            new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))),
        },
    };

    /// <summary>A 22x32 mouse outline with the pressed button filled in.</summary>
    private Canvas MouseIcon(string button)
    {
        var icon = new Canvas { Width = 22, Height = 32, Effect = Glow(8) };
        var white = Brushes.White;
        icon.Children.Add(new Path { Data = Geometry.Parse("M11,1 A10,10 0 0 1 21,11 L21,21 A10,10 0 0 1 1,21 L1,11 A10,10 0 0 1 11,1 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)), Stroke = white, StrokeThickness = 1.5 });
        var b = button.ToLowerInvariant();
        if (b.StartsWith('l'))
            icon.Children.Add(new Path { Data = Geometry.Parse("M11,1 A10,10 0 0 0 1,11 L1,13 L11,13 Z"), Fill = _accent });
        else if (b.StartsWith('r'))
            icon.Children.Add(new Path { Data = Geometry.Parse("M11,1 A10,10 0 0 1 21,11 L21,13 L11,13 Z"), Fill = _accent });
        icon.Children.Add(new Path { Data = Geometry.Parse("M11,1 L11,13 M1,13 L21,13"), Stroke = white, StrokeThickness = 1 });
        bool middle = b.StartsWith('m') || b == "wheel";
        var wheel = new Rectangle { Width = middle ? 6 : 4, Height = middle ? 9 : 7, RadiusX = 2, RadiusY = 2,
            Fill = middle ? _accent : white, Stroke = white, StrokeThickness = middle ? 1 : 0 };
        Canvas.SetLeft(wheel, middle ? 8 : 9);
        Canvas.SetTop(wheel, middle ? 3 : 4);
        icon.Children.Add(wheel);
        return icon;
    }

    private void DrawKeys(Point near, string combo)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var keys = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0) row.Children.Add(new TextBlock { Text = "+", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Effect = Glow(6) });
            row.Children.Add(KeyCap(Pretty(keys[i]), 16));
        }
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = row.DesiredSize;
        Place(row, Math.Clamp(near.X - size.Width / 2, 8, Math.Max(8, Width - size.Width - 8)), Math.Min(near.Y + 36, Height - size.Height - 8));
        var pop = new ScaleTransform(0.6, 0.6, size.Width / 2, size.Height / 2);
        row.RenderTransform = pop;
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private Border KeyCap(string text, double size)
    {
        var cap = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
            BorderBrush = _accent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(size * 0.6, size * 0.25, size * 0.6, size * 0.3),
            Effect = Glow(10),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = size, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") },
        };
        cap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        cap.Width = cap.DesiredSize.Width;
        return cap;
    }

    /// <summary>"Num0" → "Numpad 0", "ctrl" → "Ctrl".</summary>
    private static string Pretty(string key)
    {
        var k = key.Trim();
        var lower = k.ToLowerInvariant();
        foreach (var prefix in new[] { "numpad", "num", "kp_" })
            if (lower.StartsWith(prefix) && lower.Length > prefix.Length && char.IsDigit(lower[^1]))
                return "Numpad " + k[prefix.Length..].Trim();
        return string.Join("+", k.Split('+').Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        Board.Children.Add(e);
    }
}

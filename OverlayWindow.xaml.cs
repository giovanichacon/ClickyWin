using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using ClickyWindows.Helpers;
using ClickyWindows.Models;

namespace ClickyWindows;

public partial class OverlayWindow : Window
{
    // ── Dot state machine ───────────────────────────────────────────────────
    private enum DotState { FollowingCursor, FlyingToTarget, PointingAtTarget }
    private DotState _dotState = DotState.FollowingCursor;
    private AppState _appState = AppState.Idle;

    // ── Spring physics (cursor trailing) ────────────────────────────────────
    // Dot trails the cursor with a spring: responsive but with a pleasant lag.
    // Constants match original Clicky: response ~0.2 s, slight underdamp for a subtle bounce.
    private double _dotX, _dotY;    // Current dot center in WPF coords relative to this window
    private double _dotVX, _dotVY;  // Velocity
    private bool   _initialized;

    // +35 px right, 25 px above the actual cursor tip (matching macOS Clicky offsets)
    private const double CursorOffsetX =  35.0;
    private const double CursorOffsetY = -25.0;

    // Spring: k=300 → ω₀≈17 rad/s → period ≈ 0.37 s; ζ=0.72 → slight underdamp
    private const double SpringK    = 300.0;
    private const double SpringDamp =  25.0;

    // ── Flight animation (dot flies to target) ──────────────────────────────
    private double _flightStartX, _flightStartY;
    private double _flightTargetX, _flightTargetY;
    private double _flightElapsed, _flightDuration;
    private string _flightLabel = "";

    // ── Audio waveform (Listening state) ────────────────────────────────────
    // Bar relative heights from original Clicky: center bar tallest.
    private static readonly double[] BarProfiles = [0.4, 0.7, 1.0, 0.7, 0.4];
    private WpfRect[] _waveformBars = null!;
    private volatile float _audioLevel; // written by the audio thread, read by the render tick
    private double _wavePhase;

    // ── Label hold timer ────────────────────────────────────────────────────
    private DispatcherTimer? _labelTimer;

    // ── Utterance phrases (same pool as original Clicky) ───────────────────
    private static readonly string[] Utterances =
    [
        "right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"
    ];
    private static readonly Random Rng = new();

    // ── Timers ──────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _cursorTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        _waveformBars = [Bar0, Bar1, Bar2, Bar3, Bar4];

        _cursorTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60 fps
        };
        _cursorTimer.Tick += OnCursorTick;
    }

    // ── Win32 overlay setup ─────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        Win32.SetWindowPos(
            hwnd,
            Win32.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dpiScale = CoordinateHelper.GetDpiScale();
        Left   = screen.Bounds.Left   / dpiScale;
        Top    = screen.Bounds.Top    / dpiScale;
        Width  = screen.Bounds.Width  / dpiScale;
        Height = screen.Bounds.Height / dpiScale;

        _cursorTimer.Start();
    }

    // ── Main tick: spring physics + flight animation + waveform bars ─────────

    private void OnCursorTick(object? sender, EventArgs e)
    {
        const double dt = 0.016;

        Win32.GetCursorPos(out var pt);
        var dpiScale = CoordinateHelper.GetDpiScale();

        // Target position for the dot: cursor + trailing offset
        double cursorWpfX = pt.X / dpiScale - Left;
        double cursorWpfY = pt.Y / dpiScale - Top;
        double targetX = cursorWpfX + CursorOffsetX;
        double targetY = cursorWpfY + CursorOffsetY;

        // Snap to cursor on very first tick so the dot doesn't fly in from (0,0)
        if (!_initialized)
        {
            _dotX = targetX;
            _dotY = targetY;
            _initialized = true;
        }

        // ── Advance dot based on state ──────────────────────────────────────
        switch (_dotState)
        {
            case DotState.FollowingCursor:
                // Spring physics — runs even when dot is visually hidden (waveform/spinner shown)
                // so the spring is warmed up and the dot appears smoothly when re-shown.
                _dotVX += ((targetX - _dotX) * SpringK - _dotVX * SpringDamp) * dt;
                _dotVY += ((targetY - _dotY) * SpringK - _dotVY * SpringDamp) * dt;
                _dotX  += _dotVX * dt;
                _dotY  += _dotVY * dt;
                break;

            case DotState.FlyingToTarget:
                _flightElapsed += dt;
                double p = Math.Min(_flightElapsed / _flightDuration, 1.0);

                // Smoothstep easing: ease = 3t²−2t³
                double ease = p * p * (3.0 - 2.0 * p);
                _dotX = _flightStartX + (_flightTargetX - _flightStartX) * ease;
                _dotY = _flightStartY + (_flightTargetY - _flightStartY) * ease;

                // Scale pulse: grows to 1.3× at midpoint, back to 1× on landing
                double scale = 1.0 + Math.Sin(p * Math.PI) * 0.3;
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;

                if (p >= 1.0)
                {
                    DotScale.ScaleX = DotScale.ScaleY = 1.0;
                    _dotVX = _dotVY = 0;
                    // Flying dot hides; TargetPointer's center dot becomes the visual at target
                    CursorFollower.Visibility = Visibility.Collapsed;
                    _dotState = DotState.PointingAtTarget;
                    OnArrivedAtTarget();
                }
                break;

            case DotState.PointingAtTarget:
                // Dot stays put — no position update needed
                break;
        }

        // ── Update element positions ────────────────────────────────────────

        // Cursor follower (center of 32×32 grid sits on _dotX, _dotY)
        System.Windows.Controls.Canvas.SetLeft(CursorFollower, _dotX - 16);
        System.Windows.Controls.Canvas.SetTop(CursorFollower,  _dotY - 16);

        // Waveform bars: bottom of 20 px container sits at _dotY so bars grow upward from there
        if (WaveformBars.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Canvas.SetLeft(WaveformBars, _dotX - 9);
            System.Windows.Controls.Canvas.SetTop(WaveformBars,  _dotY - 20);
            UpdateWaveformBars(dt);
        }

        // Spinner: centered on _dotX, _dotY
        if (ProcessingSpinner.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Canvas.SetLeft(ProcessingSpinner, _dotX - 11);
            System.Windows.Controls.Canvas.SetTop(ProcessingSpinner,  _dotY - 11);
        }
    }

    // ── Waveform bar animation ──────────────────────────────────────────────
    // Replicates original Clicky's formula: sine idle pulse + exponential audio response.

    public void SetAudioLevel(float level) => _audioLevel = level;

    private void UpdateWaveformBars(double dt)
    {
        _wavePhase += dt * 3.6; // advances phase at ~3.6 rad/s

        double normalized = Math.Max(_audioLevel - 0.008f, 0);
        double eased = Math.Pow(Math.Min(normalized * 2.85, 1.0), 0.76);

        for (int i = 0; i < 5; i++)
        {
            double reactive = eased * 10.0 * BarProfiles[i];
            double idle     = (Math.Sin(_wavePhase + i * 0.35) + 1.0) / 2.0 * 1.5;
            _waveformBars[i].Height = Math.Max(2, Math.Min(18, 3 + reactive + idle));
        }
    }

    // ── Processing spinner ──────────────────────────────────────────────────

    private void StartSpinner()
    {
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void StopSpinner() =>
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);

    // ── Public API called by CompanionManager ───────────────────────────────

    public void SetState(AppState state)
    {
        _appState = state;
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case AppState.Idle:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    if (_dotState != DotState.FollowingCursor)
                        ResumeFollowingCursor(cancelPointer: true);
                    else
                        CursorFollower.Visibility = Visibility.Visible;
                    break;

                case AppState.Listening:
                    _labelTimer?.Stop();
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    // Cancel any in-progress pointing and return to spring following
                    if (_dotState != DotState.FollowingCursor)
                    {
                        TargetPointer.Visibility = Visibility.Collapsed;
                        TargetLabel.Visibility   = Visibility.Collapsed;
                        _dotVX = _dotVY = 0;
                        _dotState = DotState.FollowingCursor;
                    }
                    // Replace dot with animated waveform bars
                    CursorFollower.Visibility = Visibility.Collapsed;
                    WaveformBars.Visibility   = Visibility.Visible;
                    break;

                case AppState.Processing:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    CursorFollower.Visibility    = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Visible;
                    StartSpinner();
                    break;

                case AppState.Speaking:
                    WaveformBars.Visibility      = Visibility.Collapsed;
                    ProcessingSpinner.Visibility = Visibility.Collapsed;
                    StopSpinner();
                    // Show dot unless it's currently pointing (TargetPointer center dot fills that role)
                    if (_dotState != DotState.PointingAtTarget)
                        CursorFollower.Visibility = Visibility.Visible;
                    break;
            }
        });
    }

    /// <summary>
    /// Animates the cursor dot from its current position to the target screen coordinates
    /// returned by the Computer Use API. Shows the pulse ring immediately; the dot flies there.
    /// </summary>
    public void ShowTargetAt(double physX, double physY, string label)
    {
        Dispatcher.Invoke(() =>
        {
            var dpiScale = CoordinateHelper.GetDpiScale();
            double wpfX = physX / dpiScale - Left;
            double wpfY = physY / dpiScale - Top;

            // Pulse ring appears at target immediately
            System.Windows.Controls.Canvas.SetLeft(TargetPointer, wpfX - 24);
            System.Windows.Controls.Canvas.SetTop(TargetPointer,  wpfY - 24);
            TargetPointer.Visibility = Visibility.Visible;
            AnimatePulse();

            // Begin flight from current dot position to target
            _flightStartX  = _dotX;
            _flightStartY  = _dotY;
            _flightTargetX = wpfX;
            _flightTargetY = wpfY;
            _flightElapsed = 0;
            _flightLabel   = label;

            double dist = Math.Sqrt(
                (_flightTargetX - _dotX) * (_flightTargetX - _dotX) +
                (_flightTargetY - _dotY) * (_flightTargetY - _dotY));
            // Duration: 0.5–1.4 s based on distance (from original Clicky)
            _flightDuration = Math.Clamp(dist / 800.0, 0.5, 1.4);

            _labelTimer?.Stop();
            CursorFollower.Visibility = Visibility.Visible;
            _dotState = DotState.FlyingToTarget;
        });
    }

    // Called on the Dispatcher thread when the flight animation completes
    private void OnArrivedAtTarget()
    {
        // Label positioned to the right of and slightly below the target center
        System.Windows.Controls.Canvas.SetLeft(TargetLabel, _flightTargetX + 20);
        System.Windows.Controls.Canvas.SetTop(TargetLabel,  _flightTargetY + 12);

        // Use a random utterance phrase (same pool as original Clicky)
        TargetLabelText.Text = Utterances[Rng.Next(Utterances.Length)];
        TargetLabel.Opacity    = 0;
        TargetLabel.Visibility = Visibility.Visible;

        // Fade in
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
        TargetLabel.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        // Hold for 3 s, then fade out and resume cursor following
        _labelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.3) };
        _labelTimer.Tick += (_, _) =>
        {
            _labelTimer!.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
            fadeOut.Completed += (_, _) =>
            {
                TargetLabel.Visibility = Visibility.Collapsed;
                ResumeFollowingCursor(cancelPointer: true);
            };
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        _labelTimer.Start();
    }

    /// <summary>
    /// Hides the target pointer and label, cancels any pending timer,
    /// and resumes spring cursor following. The spring naturally animates
    /// the dot back to the cursor from wherever it currently sits.
    /// </summary>
    private void ResumeFollowingCursor(bool cancelPointer)
    {
        _labelTimer?.Stop();

        if (cancelPointer)
        {
            TargetPointer.Visibility = Visibility.Collapsed;
            TargetLabel.Visibility   = Visibility.Collapsed;
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, null);
        }

        _dotVX    = 0;
        _dotVY    = 0;
        _dotState = DotState.FollowingCursor;

        // Show dot only if the app isn't in a state that uses a different indicator
        if (_appState != AppState.Listening && _appState != AppState.Processing)
            CursorFollower.Visibility = Visibility.Visible;
    }

    // ── Feedback bubble (silent failures, error messages) ───────────────────

    /// <summary>
    /// Shows a transient message bubble near the cursor dot — used for silent failures
    /// so the user always knows what happened. Reuses the same TargetLabel element.
    /// </summary>
    public void ShowFeedback(string message)
    {
        Dispatcher.Invoke(() =>
        {
            _labelTimer?.Stop();

            // Position near current dot location
            System.Windows.Controls.Canvas.SetLeft(TargetLabel, _dotX + 20);
            System.Windows.Controls.Canvas.SetTop(TargetLabel,  _dotY + 12);

            TargetLabelText.Text    = message;
            TargetLabel.Opacity     = 0;
            TargetLabel.Visibility  = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25));
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            // Hold 3 s then fade out; don't change dot state
            _labelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.25) };
            _labelTimer.Tick += (_, _) =>
            {
                _labelTimer!.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                fadeOut.Completed += (_, _) => TargetLabel.Visibility = Visibility.Collapsed;
                TargetLabel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            _labelTimer.Start();
        });
    }

    // ── Spinner transcript-confirmation pulse ───────────────────────────────

    /// <summary>
    /// Brief scale-throb + pink color flash on the spinner to signal "I heard you."
    /// Called the moment AssemblyAI delivers the final transcript.
    /// </summary>
    public void PulseSpinner()
    {
        Dispatcher.Invoke(() =>
        {
            if (ProcessingSpinner.Visibility != Visibility.Visible) return;

            // Scale: 1.0 → 1.45 → 1.0 over 0.5 s
            var scaleAnim = new DoubleAnimationUsingKeyFrames();
            scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

            SpinnerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            // Color: blue → light pink → blue over the same duration
            var colorAnim = new ColorAnimationUsingKeyFrames();
            colorAnim.KeyFrames.Add(new LinearColorKeyFrame(
                WpfColor.FromRgb(0x33, 0x80, 0xFF), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            colorAnim.KeyFrames.Add(new EasingColorKeyFrame(
                WpfColor.FromRgb(0xFF, 0x8A, 0xBA), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            colorAnim.KeyFrames.Add(new LinearColorKeyFrame(
                WpfColor.FromRgb(0x33, 0x80, 0xFF), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));

            SpinnerStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        });
    }

    // ── Pulse ring animation ────────────────────────────────────────────────

    private void AnimatePulse()
    {
        var scaleAnim = new DoubleAnimation(0.5, 2.0, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var opacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = new RepeatBehavior(3),
        };

        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);
        PulseRing.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }
}

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
    private bool _alwaysShowCursor = true;
    private bool _hwndReady;
    private IntPtr _hwnd;
    private double _dpiScale = 1.0;   // cached: reading it costs a GDI call, too much at 60 fps
    private int _winX = int.MinValue, _winY = int.MinValue;

    // The window is small and moves with the dot (a full-screen layered window costs tens of MB).
    // The dot center always sits at this local point; the XAML places every element around it.
    private const double DotLocalX = 60.0;
    private const double DotLocalY = 60.0;

    // ── Spring physics (cursor trailing) ────────────────────────────────────
    // Dot trails the cursor with a spring: responsive but with a pleasant lag.
    // Constants match original Clicky: response ~0.2 s, slight underdamp for a subtle bounce.
    private double _dotX, _dotY;    // Current dot center in screen DIPs (physical px / DPI scale)
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

    // ── Utterance phrases (same pool as original Clicky, localized in L) ───
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
        var hwnd = _hwnd = new WindowInteropHelper(this).Handle;

        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        Win32.SetWindowPos(
            hwnd,
            Win32.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        // The spinner/dot must not end up in the screenshots Claude sees
        if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Logger.Log($"[Overlay] Could not exclude the overlay from screenshots (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");

        _hwndReady = true;
        UpdateTicking();
    }

    /// <summary>false = the dot appears only while listening, thinking, speaking or pointing.</summary>
    public void SetAlwaysShowCursor(bool always)
    {
        _alwaysShowCursor = always;
        if (_appState == AppState.Idle && _dotState == DotState.FollowingCursor)
            CursorFollower.Visibility = always ? Visibility.Visible : Visibility.Collapsed;
        UpdateTicking();
    }

    /// <summary>
    /// Runs the 60 fps tick only while something is on screen, so a hidden idle Clicky costs no CPU.
    /// On restart the dot snaps to the cursor instead of flying in from where it last stopped.
    /// </summary>
    private void UpdateTicking()
    {
        if (!_hwndReady) return;
        bool needed = _alwaysShowCursor || _appState != AppState.Idle ||
                      _dotState != DotState.FollowingCursor || TargetLabel.Visibility == Visibility.Visible;
        if (needed && !_cursorTimer.IsEnabled)
        {
            SnapToCursor();
            _cursorTimer.Start();
        }
        else if (!needed && _cursorTimer.IsEnabled)
        {
            _cursorTimer.Stop();
        }
    }

    private void SnapToCursor()
    {
        Win32.GetCursorPos(out var pt);
        _dpiScale = CoordinateHelper.GetDpiScale();
        _dotX = pt.X / _dpiScale + CursorOffsetX;
        _dotY = pt.Y / _dpiScale + CursorOffsetY;
        _dotVX = _dotVY = 0;
        _initialized = true;
        MoveToDot(); // before anything shows, so nothing flashes where the window last stopped
    }

    /// <summary>Moves the window so its local dot point lands on the dot's screen position.</summary>
    private void MoveToDot()
    {
        if (!_hwndReady) return;
        int x = (int)Math.Round((_dotX - DotLocalX) * _dpiScale);
        int y = (int)Math.Round((_dotY - DotLocalY) * _dpiScale);
        if (x == _winX && y == _winY) return;
        _winX = x;
        _winY = y;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    // ── Main tick: spring physics + flight animation + waveform bars ─────────

    private void OnCursorTick(object? sender, EventArgs e)
    {
        const double dt = 0.016;

        Win32.GetCursorPos(out var pt);

        // Target position for the dot: cursor + trailing offset
        double cursorWpfX = pt.X / _dpiScale;
        double cursorWpfY = pt.Y / _dpiScale;
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

        // ── Move the window; elements are fixed around the local dot point ──
        MoveToDot();

        // Waveform bars: bottom of the 20 px container sits on the dot so bars grow upward
        if (WaveformBars.Visibility == Visibility.Visible)
            UpdateWaveformBars(dt);
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
                        CursorFollower.Visibility = _alwaysShowCursor ? Visibility.Visible : Visibility.Collapsed;
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
            UpdateTicking();
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
            if (!_cursorTimer.IsEnabled) SnapToCursor(); // hidden idle dot: fly out from the cursor
            double wpfX = physX / _dpiScale;
            double wpfY = physY / _dpiScale;

            // The pulse ring rides in the dot's window, so it starts when the dot lands (OnArrivedAtTarget)
            TargetPointer.Visibility = Visibility.Collapsed;

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
            UpdateTicking();
        });
    }

    // Called on the Dispatcher thread when the flight animation completes
    private void OnArrivedAtTarget()
    {
        // Pulse ring on the target; the label sits right of and slightly below it (fixed in XAML)
        TargetPointer.Visibility = Visibility.Visible;
        AnimatePulse();

        // Use a random utterance phrase (same pool as original Clicky)
        var utterances = L.Utterances;
        TargetLabelText.Text = utterances[Rng.Next(utterances.Length)];
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
        if (_appState == AppState.Speaking || (_appState == AppState.Idle && _alwaysShowCursor))
            CursorFollower.Visibility = Visibility.Visible;
        else if (_appState == AppState.Idle)
            CursorFollower.Visibility = Visibility.Collapsed;
        UpdateTicking();
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
            if (!_cursorTimer.IsEnabled) SnapToCursor(); // hidden idle dot: show the bubble at the cursor

            // The label sits next to the dot (fixed in XAML)
            TargetLabelText.Text    = message;
            TargetLabel.Opacity     = 0;
            TargetLabel.Visibility  = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25));
            TargetLabel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            UpdateTicking();

            // Hold 3 s then fade out; don't change dot state
            _labelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.25) };
            _labelTimer.Tick += (_, _) =>
            {
                _labelTimer!.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                fadeOut.Completed += (_, _) =>
                {
                    TargetLabel.Visibility = Visibility.Collapsed;
                    UpdateTicking();
                };
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

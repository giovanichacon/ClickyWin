using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows.Threading;
using ClickyWindows.Helpers;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// First-run and tray "Settings…" window: API keys (with Test buttons), voice, microphone
/// (with a live level meter), models and language. Keys left empty keep the saved value.
/// </summary>
public partial class SetupWindow : Window
{
    private static readonly (string id, string name)[] Models =
    [
        ("claude-haiku-5-5", "Haiku 5.5  ($)"),
        ("claude-sonnet-5-5", "Sonnet 5.5  ($$)"),
        ("claude-opus-5-5", "Opus 5.5  ($$$)"),
    ];

    private readonly AppSettings _settings;
    private readonly MicLevelMeter _meter = new();
    private readonly DispatcherTimer _meterTimer;
    private int _meterGeneration;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        Title = L.SetupTitle;
        IntroText.Text = L.SetupIntro;
        ClaudeLabel.Text = L.KeyClaude;
        AssemblyLabel.Text = L.KeyAssembly;
        ElevenLabel.Text = L.KeyEleven;
        VoiceLabel.Text = L.VoiceLabel;
        MicLabel.Text = L.MicLabel;
        MicHint.Text = L.MicHint;
        ModelLabel.Text = L.ModelLabel;
        EscalationLabel.Text = L.EscalationLabel;
        LanguageLabel.Text = L.LanguageLabel;
        ClaudeTest.Content = AssemblyTest.Content = ElevenTest.Content = L.Test;
        SaveButton.Content = L.Save;
        CancelButton.Content = L.Cancel;

        ClaudeStatus.Text = KeyState(settings.AnthropicApiKey);
        AssemblyStatus.Text = KeyState(settings.AssemblyAiApiKey);
        ElevenStatus.Text = KeyState(settings.ElevenLabsApiKey);

        Fill(VoiceBox, settings.Voice,
            ("Auto", L.VoiceAuto), ("ElevenLabs", L.VoiceEleven), ("Windows", L.VoiceWindows));
        Fill(ModelBox, settings.EffectivePrimaryModel, Models);
        Fill(EscalationBox, settings.EffectiveEscalationModel ?? "none",
            [.. Models.Where(m => m.id != "claude-haiku-5-5"), ("none", L.EscalationOff)]);
        Fill(LanguageBox, settings.Language,
            ("Auto", L.LanguageAuto), ("es", "Español"), ("en", "English"));

        var mics = AudioCaptureService.ListDevices();
        Fill(MicBox, settings.MicDeviceId, [("", L.MicDefault), .. mics]);

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) =>
            MicLevel.Value = Math.Max(_meter.TakePeak(), MicLevel.Value * 0.8); // fast attack, smooth fall
        _meterTimer.Start();

        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            _meter.Dispose();
        };
    }

    private static string KeyState(string key) =>
        string.IsNullOrWhiteSpace(key) ? L.NotSet
        : key.Trim().Length > 8 ? $"{L.Saved} (…{key.Trim()[^4..]})" : L.Saved;

    private static void Fill(ComboBox box, string selected, params (string value, string text)[] items)
    {
        foreach (var (value, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            box.Items.Add(item);
            if (value == selected) box.SelectedItem = item;
        }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    // ── Microphone preview ──────────────────────────────────────────────────

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        var deviceId = Selected(MicBox);
        int generation = ++_meterGeneration;
        MicLevel.Value = 0;
        // Opening a device can take up to a second: keep the window responsive
        _ = Task.Run(() =>
        {
            if (generation == _meterGeneration) _meter.Start(deviceId);
        });
    }

    // ── Key tests ───────────────────────────────────────────────────────────

    private async void OnTestClaude(object sender, RoutedEventArgs e) =>
        await RunTest(ClaudeKey, _settings.AnthropicApiKey, ClaudeStatus, ClaudeTest,
            key => ClaudeService.TestKeyAsync(key, _settings.Config.Endpoints.Anthropic, Selected(ModelBox)));

    private async void OnTestAssembly(object sender, RoutedEventArgs e) =>
        await RunTest(AssemblyKey, _settings.AssemblyAiApiKey, AssemblyStatus, AssemblyTest,
            AssemblyAIService.TestKeyAsync);

    private async void OnTestEleven(object sender, RoutedEventArgs e) =>
        await RunTest(ElevenKey, _settings.ElevenLabsApiKey, ElevenStatus, ElevenTest,
            key => ElevenLabsService.TestKeyAsync(key, _settings.Config.Endpoints.ElevenLabs));

    private static async Task RunTest(PasswordBox box, string savedKey, TextBlock status, Button button,
        Func<string, Task<(bool ok, string message)>> test)
    {
        var key = string.IsNullOrWhiteSpace(box.Password) ? savedKey : box.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            status.Text = L.NotSet;
            return;
        }

        button.IsEnabled = false;
        status.Text = L.Testing;
        try
        {
            var (ok, message) = await test(key);
            status.Text = ok ? $"{L.TestOk} — {message}" : L.TestFail(message);
            status.Foreground = ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Firebrick;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ── Save / cancel ───────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ClaudeKey.Password)) _settings.AnthropicApiKey = ClaudeKey.Password.Trim();
        if (!string.IsNullOrWhiteSpace(AssemblyKey.Password)) _settings.AssemblyAiApiKey = AssemblyKey.Password.Trim();
        if (!string.IsNullOrWhiteSpace(ElevenKey.Password)) _settings.ElevenLabsApiKey = ElevenKey.Password.Trim();
        _settings.Voice = Selected(VoiceBox);
        _settings.MicDeviceId = Selected(MicBox);
        _settings.PrimaryModel = Selected(ModelBox);
        _settings.EscalationModel = Selected(EscalationBox);
        _settings.Language = Selected(LanguageBox);

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] Save failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, L.SaveFailed(ex.Message), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        L.Setting = _settings.Language;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

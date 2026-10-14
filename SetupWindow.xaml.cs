using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;
using ClickyWindows.Helpers;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// First-run and tray "Settings…" window, in tabs: API keys (with Test buttons); voice, speed and
/// microphone (live level meter); shortcuts (recorded through the keyboard hook, so modifier-only
/// combos like Ctrl+Win work); dictation (cleanup, learning, dictionary); general options.
/// Keys left empty keep the saved value.
/// </summary>
public partial class SetupWindow : Window
{
    private static readonly (string id, string name)[] Models =
    [
        ("claude-haiku-5-5", "Haiku 5.5  ($)"),
        ("claude-sonnet-5-5", "Sonnet 5.5  ($$)"),
        ("claude-opus-5-5", "Opus 5.5  ($$$)"),
    ];

    private const string OffValue = "Off";

    private readonly AppSettings _settings;
    private readonly KeyboardHookService? _keys;
    private readonly MicLevelMeter _meter = new();
    private readonly DispatcherTimer _meterTimer;
    private int _meterGeneration;
    private int _voiceGeneration;
    private bool _voiceListIsElevenLabs;
    private bool _loaded;

    // Shortcut text per action ("Off" = disabled), shown in the matching box
    private readonly Dictionary<ShortcutAction, string> _shortcuts = new();
    private readonly Dictionary<ShortcutAction, TextBox> _shortcutBoxes;

    public SetupWindow(AppSettings settings, KeyboardHookService? keys = null)
    {
        InitializeComponent();
        _settings = settings;
        _keys = keys;
        _shortcutBoxes = new()
        {
            [ShortcutAction.Ask] = AskBox,
            [ShortcutAction.Dictate] = DictateBox,
            [ShortcutAction.Write] = WriteBox,
            [ShortcutAction.Prompt] = PromptBox,
        };

        Title = L.SetupTitle;
        KeysTab.Header = L.TabKeys;
        VoiceTab.Header = L.TabVoice;
        ShortcutsTab.Header = L.TabShortcuts;
        DictationTab.Header = L.TabDictation;
        GeneralTab.Header = L.TabGeneral;
        SaveButton.Content = L.Save;
        CancelButton.Content = L.Cancel;

        // Keys
        IntroText.Text = L.SetupIntro;
        ClaudeLabel.Text = L.KeyClaude;
        AssemblyLabel.Text = L.KeyAssembly;
        ElevenLabel.Text = L.KeyEleven;
        ClaudeTest.Content = AssemblyTest.Content = ElevenTest.Content = L.Test;
        ClaudeStatus.Text = KeyState(settings.AnthropicApiKey);
        AssemblyStatus.Text = KeyState(settings.AssemblyAiApiKey);
        ElevenStatus.Text = KeyState(settings.ElevenLabsApiKey);

        // Voice
        VoiceLabel.Text = L.VoiceLabel;
        VoiceNameLabel.Text = L.VoiceNameLabel;
        PreviewButton.Content = L.Preview;
        VoiceSpeedLabel.Text = L.VoiceSpeedLabel;
        VoiceSpeedSlider.Value = settings.EffectiveVoiceSpeed;
        VoiceSpeedValue.Text = $"{settings.EffectiveVoiceSpeed:0.00}×";
        MicLabel.Text = L.MicLabel;
        MicHint.Text = L.MicHint;
        Fill(VoiceBox, settings.Voice,
            ("Auto", L.VoiceAuto), ("ElevenLabs", L.VoiceEleven), ("Windows", L.VoiceWindows));
        Fill(MicBox, settings.MicDeviceId, [("", L.MicDefault), .. AudioCaptureService.ListDevices()]);

        // Shortcuts
        AskLabel.Text = L.HotkeyAsk;
        DictateLabel.Text = L.HotkeyDictate;
        WriteLabel.Text = L.HotkeyWrite;
        PromptLabel.Text = L.HotkeyPrompt;
        AskChange.Content = DictateChange.Content = WriteChange.Content = PromptChange.Content = L.HotkeyChange;
        AskReset.Content = DictateReset.Content = WriteReset.Content = PromptReset.Content = L.HotkeyDefault;
        DictateOff.Content = WriteOff.Content = PromptOff.Content = L.HotkeyOff;
        ShortcutHelp.Text = L.HotkeyHelp;
        SetShortcut(ShortcutAction.Ask, settings.EffectiveHotkey);
        SetShortcut(ShortcutAction.Dictate, settings.EffectiveDictateHotkey);
        SetShortcut(ShortcutAction.Write, settings.EffectiveWriteHotkey);
        SetShortcut(ShortcutAction.Prompt, settings.EffectivePromptHotkey);

        // Dictation
        CleanupBox.Content = L.CleanupLabel;
        CleanupBox.IsChecked = settings.DictationCleanup;
        LearnBox.Content = L.LearnLabel;
        LearnBox.IsChecked = settings.LearnWords;
        DictionaryLabel.Text = L.DictionaryLabel;
        DictionaryHint.Text = L.DictionaryHint;
        DictionaryBox.Text = string.Join(Environment.NewLine, settings.Dictionary);

        // General
        ModelLabel.Text = L.ModelLabel;
        EscalationLabel.Text = L.EscalationLabel;
        LanguageLabel.Text = L.LanguageLabel;
        Fill(ModelBox, settings.EffectivePrimaryModel, Models);
        Fill(EscalationBox, settings.EffectiveEscalationModel ?? "none",
            [.. Models.Where(m => m.id != "claude-haiku-5-5"), ("none", L.EscalationOff)]);
        Fill(LanguageBox, settings.Language,
            ("Auto", L.LanguageAuto), ("es", "Español"), ("en", "English"));
        AlwaysCursorBox.Content = L.AlwaysShowCursor;
        AlwaysCursorBox.IsChecked = settings.AlwaysShowCursor;
        CopyAnswersBox.Content = L.CopyAnswersLabel;
        CopyAnswersBox.IsChecked = settings.CopyAnswers;
        TutorBox.Content = L.TutorLabel;
        TutorBox.IsChecked = settings.TutorMode;
        AutostartBox.Content = L.AutostartLabel;
        AutostartBox.IsChecked = Autostart.IsEnabled;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) =>
            MicLevel.Value = Math.Max(_meter.TakePeak(), MicLevel.Value * 0.8); // fast attack, smooth fall
        _meterTimer.Start();

        _loaded = true;
        RefreshVoiceNames();

        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            _meter.Dispose();
            _keys?.CancelRecording();
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

    private static void SetStatus(TextBlock block, string text, bool error)
    {
        block.Text = text;
        block.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

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

    // ── Shortcuts ───────────────────────────────────────────────────────────

    private static ShortcutAction ActionOf(object sender) => ((FrameworkElement)sender).Name switch
    {
        var n when n.StartsWith("Dictate") => ShortcutAction.Dictate,
        var n when n.StartsWith("Write") => ShortcutAction.Write,
        var n when n.StartsWith("Prompt") => ShortcutAction.Prompt,
        _ => ShortcutAction.Ask,
    };

    private string DefaultShortcut(ShortcutAction action) => action switch
    {
        ShortcutAction.Dictate => _settings.Config.DictateHotkey,
        ShortcutAction.Write => _settings.Config.WriteHotkey,
        ShortcutAction.Prompt => _settings.Config.PromptHotkey,
        _ => _settings.Config.Hotkey,
    };

    private void SetShortcut(ShortcutAction action, string text)
    {
        var normalized = text.Equals(OffValue, StringComparison.OrdinalIgnoreCase) ? OffValue
            : HotkeyParser.TryParse(text, out var chord) ? HotkeyParser.Format(chord)
            : HotkeyParser.ParseOrDefault(DefaultShortcut(action)).text;
        _shortcuts[action] = normalized;
        _shortcutBoxes[action].Text = normalized == OffValue ? L.Off : normalized;
    }

    private void OnShortcutChange(object sender, RoutedEventArgs e)
    {
        var action = ActionOf(sender);
        if (_keys == null) return;

        var box = _shortcutBoxes[action];
        box.Text = L.HotkeyPress;
        box.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6));
        SetStatus(ShortcutStatus, "", error: false);

        _keys.BeginRecording(text => Dispatcher.InvokeAsync(() =>
        {
            box.ClearValue(BackgroundProperty);
            if (text == null)
            {
                SetShortcut(action, _shortcuts[action]); // Esc: keep the old one
                return;
            }
            if (!HotkeyParser.TryParse(text, out _))
            {
                SetShortcut(action, _shortcuts[action]);
                SetStatus(ShortcutStatus, L.HotkeyInvalid, error: true);
                return;
            }
            SetShortcut(action, text);
        }));
    }

    private void OnShortcutReset(object sender, RoutedEventArgs e)
    {
        var action = ActionOf(sender);
        SetShortcut(action, DefaultShortcut(action));
        SetStatus(ShortcutStatus, "", error: false);
    }

    private void OnShortcutOff(object sender, RoutedEventArgs e) => SetShortcut(ActionOf(sender), OffValue);

    // ── Voices ──────────────────────────────────────────────────────────────

    private void OnVoiceEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) RefreshVoiceNames();
    }

    private void OnVoiceSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VoiceSpeedValue != null) VoiceSpeedValue.Text = $"{e.NewValue:0.00}×";
    }

    private string ElevenKeyInUse =>
        string.IsNullOrWhiteSpace(ElevenKey.Password) ? _settings.ElevenLabsApiKey : ElevenKey.Password.Trim();

    /// <summary>Lists ElevenLabs voices when ElevenLabs will speak, else the installed Windows voices.</summary>
    private async void RefreshVoiceNames()
    {
        int generation = ++_voiceGeneration;
        var engine = Selected(VoiceBox);
        var key = ElevenKeyInUse;
        bool useElevenLabs = engine == "ElevenLabs" || (engine == "Auto" && !string.IsNullOrWhiteSpace(key));

        VoiceNameBox.Items.Clear();
        SetStatus(VoiceNameStatus, "", error: false);
        _voiceListIsElevenLabs = useElevenLabs;

        if (!useElevenLabs || string.IsNullOrWhiteSpace(key))
        {
            var installed = WindowsVoiceService.InstalledVoices();
            Fill(VoiceNameBox, _settings.WindowsVoice,
                [("", L.VoiceByLanguage), .. installed.Select(v => (v.name, $"{v.name}  ({v.culture})"))]);
            _voiceListIsElevenLabs = false;
            return;
        }

        VoiceNameStatus.Text = L.LoadingVoices;
        try
        {
            var voices = await ElevenLabsService.ListVoicesAsync(key, _settings.Config.Endpoints.ElevenLabs);
            if (generation != _voiceGeneration) return;
            Fill(VoiceNameBox, _settings.EffectiveElevenLabsVoiceId, [.. voices]);
            VoiceNameStatus.Text = L.ElevenFreeNote;
        }
        catch (Exception ex)
        {
            if (generation != _voiceGeneration) return;
            SetStatus(VoiceNameStatus, L.VoicesFailed(ex.Message), error: true);
        }
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        var voice = Selected(VoiceNameBox);
        double speed = VoiceSpeedSlider.Value;
        PreviewButton.IsEnabled = false;
        try
        {
            if (_voiceListIsElevenLabs)
            {
                using var eleven = new ElevenLabsService(_settings);
                await eleven.SpeakWithAsync(L.PreviewText, ElevenKeyInUse, voice, speed, null);
            }
            else
            {
                using var windows = new WindowsVoiceService(() => voice, () => speed);
                await Task.Run(() => windows.SpeakAsync(L.PreviewText, null, CancellationToken.None));
            }
            VoiceNameStatus.Foreground = Brushes.DimGray;
        }
        catch (Exception ex)
        {
            SetStatus(VoiceNameStatus, L.TestFail(ex.Message), error: true);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

    // ── Key tests ───────────────────────────────────────────────────────────

    private async void OnTestClaude(object sender, RoutedEventArgs e) =>
        await RunTest(ClaudeKey, _settings.AnthropicApiKey, ClaudeStatus, ClaudeTest,
            key => ClaudeService.TestKeyAsync(key, _settings.Config.Endpoints.Anthropic, Selected(ModelBox)));

    private async void OnTestAssembly(object sender, RoutedEventArgs e) =>
        await RunTest(AssemblyKey, _settings.AssemblyAiApiKey, AssemblyStatus, AssemblyTest,
            AssemblyAIService.TestKeyAsync);

    private async void OnTestEleven(object sender, RoutedEventArgs e)
    {
        await RunTest(ElevenKey, _settings.ElevenLabsApiKey, ElevenStatus, ElevenTest,
            key => ElevenLabsService.TestKeyAsync(key, _settings.Config.Endpoints.ElevenLabs));
        RefreshVoiceNames();
    }

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
            status.Foreground = ok ? Brushes.SeaGreen : Brushes.Firebrick;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ── Save / cancel ───────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Two actions on the same combination would fight over it
        var duplicate = _shortcuts.Values.Where(v => v != OffValue).GroupBy(v => v).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            Tabs.SelectedItem = ShortcutsTab;
            SetStatus(ShortcutStatus, L.HotkeyDuplicate(duplicate.Key), error: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(ClaudeKey.Password)) _settings.AnthropicApiKey = ClaudeKey.Password.Trim();
        if (!string.IsNullOrWhiteSpace(AssemblyKey.Password)) _settings.AssemblyAiApiKey = AssemblyKey.Password.Trim();
        if (!string.IsNullOrWhiteSpace(ElevenKey.Password)) _settings.ElevenLabsApiKey = ElevenKey.Password.Trim();

        _settings.Voice = Selected(VoiceBox);
        if (VoiceNameBox.SelectedItem != null)
        {
            if (_voiceListIsElevenLabs) _settings.ElevenLabsVoiceId = Selected(VoiceNameBox);
            else _settings.WindowsVoice = Selected(VoiceNameBox);
        }
        _settings.VoiceSpeed = Math.Round(VoiceSpeedSlider.Value, 2);
        _settings.MicDeviceId = Selected(MicBox);

        // Stored empty when equal to the shipped default, so a new default reaches the user
        _settings.Hotkey = StoredShortcut(ShortcutAction.Ask);
        _settings.DictateHotkey = StoredShortcut(ShortcutAction.Dictate);
        _settings.WriteHotkey = StoredShortcut(ShortcutAction.Write);
        _settings.PromptHotkey = StoredShortcut(ShortcutAction.Prompt);

        _settings.DictationCleanup = CleanupBox.IsChecked == true;
        _settings.LearnWords = LearnBox.IsChecked == true;
        _settings.Dictionary = DictionaryBox.Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings.PrimaryModel = Selected(ModelBox);
        _settings.EscalationModel = Selected(EscalationBox);
        _settings.Language = Selected(LanguageBox);
        _settings.AlwaysShowCursor = AlwaysCursorBox.IsChecked == true;
        _settings.CopyAnswers = CopyAnswersBox.IsChecked == true;
        _settings.TutorMode = TutorBox.IsChecked == true;

        try
        {
            _settings.Save();
            if (AutostartBox.IsChecked == true != Autostart.IsEnabled)
                Autostart.Set(AutostartBox.IsChecked == true);
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

    private string StoredShortcut(ShortcutAction action)
    {
        var value = _shortcuts[action];
        return value == HotkeyParser.ParseOrDefault(DefaultShortcut(action)).text ? "" : value;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

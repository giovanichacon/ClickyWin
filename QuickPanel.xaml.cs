using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Small panel above the tray icon (left click): status, shortcuts, the last question and
/// answer with a Copy button, recent dictations (click to copy) and a tutor-mode toggle.
/// Closes when it loses focus, like the macOS menu-bar panel.
/// </summary>
public partial class QuickPanel : Window
{
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private string _answer = "";

    public sealed record DictationItem(string Text, string Meta, string Full);

    public QuickPanel(AppSettings settings, Action openSettings)
    {
        InitializeComponent();
        _settings = settings;
        _openSettings = openSettings;
        Deactivated += (_, _) => Hide();
        Localize();
        SetState(AppState.Idle);
        SetAnswer("", "");
        SetDictations([]);
    }

    public void Localize()
    {
        AnswerLabel.Text = L.PanelLastAnswer;
        DictationsLabel.Text = L.PanelDictations;
        CopyAnswerButton.Content = L.PanelCopy;
        TutorBox.Content = new TextBlock { Text = L.TutorLabel, TextWrapping = TextWrapping.Wrap };
        TutorBox.IsChecked = _settings.TutorMode;
    }

    public void SetShortcuts(string ask, string dictate, string write, string prompt) =>
        ShortcutsText.Text = L.PanelShortcuts(ask, dictate, write, prompt);

    public void SetState(AppState state)
    {
        (StatusText.Text, var color) = state switch
        {
            AppState.Listening => (L.PanelListening, Color.FromRgb(0xE1, 0x1D, 0x48)),
            AppState.Processing => (L.PanelThinking, Color.FromRgb(0xF5, 0x9E, 0x0B)),
            AppState.Speaking => (L.PanelSpeaking, Color.FromRgb(0x16, 0xA3, 0x4A)),
            _ => (L.PanelReady, Color.FromRgb(0x25, 0x63, 0xEB)),
        };
        StatusDot.Fill = new SolidColorBrush(color);
    }

    public void SetAnswer(string question, string answer)
    {
        _answer = answer;
        QuestionText.Text = question.Length > 0 ? $"“{question}”" : "";
        QuestionText.Visibility = question.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerText.Text = answer.Length > 0 ? answer : L.PanelNoAnswer;
        CopyAnswerButton.IsEnabled = answer.Length > 0;
        CopyAnswerButton.Content = L.PanelCopy;
    }

    public void SetDictations(IEnumerable<DictationEntry> entries)
    {
        DictationList.ItemsSource = entries
            .Select(e => new DictationItem(
                e.Text.ReplaceLineEndings(" "),
                $"{e.At:HH:mm} · {e.Mode switch { DictationMode.Write => "✎ ", DictationMode.Prompt => "➜ ", _ => "" }}{e.App}",
                e.Text))
            .ToList();
        DictationsLabel.Visibility = DictationList.Visibility =
            DictationList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the panel just above the taskbar, at the right edge of the work area.</summary>
    public void ShowNearTray()
    {
        var area = SystemParameters.WorkArea;
        Show();
        UpdateLayout();
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        Activate();
    }

    private void OnCopyAnswer(object sender, RoutedEventArgs e)
    {
        if (_answer.Length > 0 && TextInserter.SetClipboardText(_answer))
            CopyAnswerButton.Content = L.PanelCopied;
    }

    private void OnDictationPicked(object sender, SelectionChangedEventArgs e)
    {
        if (DictationList.SelectedItem is DictationItem item && TextInserter.SetClipboardText(item.Full))
        {
            DictationsLabel.Text = L.PanelCopied;
            DictationList.SelectedItem = null;
        }
    }

    private void OnTutorToggled(object sender, RoutedEventArgs e)
    {
        _settings.TutorMode = TutorBox.IsChecked == true;
        try { _settings.Save(); }
        catch (Exception ex) { Logger.Log($"[Panel] Could not save tutor mode: {ex.Message}"); }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettings();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        DictationsLabel.Text = L.PanelDictations;
    }
}

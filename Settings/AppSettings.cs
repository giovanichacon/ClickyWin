using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickyWindows.Settings;

public class AppSettings
{
    // API keys — loaded from settings file, never hardcoded
    public string AnthropicApiKey { get; set; } = "";
    public string ElevenLabsApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM"; // default ElevenLabs voice
    public string AssemblyAiApiKey { get; set; } = "";

    // Optional Cloudflare proxy URLs (leave empty to call APIs directly)
    public string ClaudeProxyUrl { get; set; } = "";
    public string ElevenLabsProxyUrl { get; set; } = "";
    public string AssemblyAiTokenUrl { get; set; } = "";

    // Push-to-talk hotkey — default: Ctrl+Shift+Space
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // MOD_CONTROL | MOD_SHIFT
    public uint HotkeyVirtualKey { get; set; } = 0x20; // VK_SPACE

    // Open the mic briefly at startup so the first press starts recording instantly
    // (the Windows mic indicator blinks once at launch)
    public bool MicWarmUp { get; set; } = true;

    // AssemblyAI streaming model — universal-3-6-pro code-switches Spanish/English automatically
    public string AssemblyAiSpeechModel { get; set; } = "universal-3-6-pro";

    // Claude model
    public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

    // Overlay settings
    public bool ShowCursorOverlay { get; set; } = true;

    // ── Persistence ────────────────────────────────────────────────────────

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindows",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* ignore, use defaults */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* ignore */ }
    }

    // Convenience: which API to call for Claude
    public string ClaudeApiUrl =>
        !string.IsNullOrWhiteSpace(ClaudeProxyUrl)
            ? ClaudeProxyUrl
            : "https://api.anthropic.com/v1/messages";

    public string ElevenLabsApiUrl(string voiceId) =>
        !string.IsNullOrWhiteSpace(ElevenLabsProxyUrl)
            ? ElevenLabsProxyUrl
            : $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
}

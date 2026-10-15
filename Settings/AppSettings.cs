using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickyWindows.Helpers;

namespace ClickyWindows.Settings;

/// <summary>
/// Per-user settings in %APPDATA%\ClickyWindows\settings.json, written by the Setup window.
/// API keys are kept in memory as plain strings but stored encrypted with Windows DPAPI
/// (CurrentUser scope): the file is useless on another account or machine.
/// Shipped defaults (prompts, hotkey, endpoints) come from <see cref="Config"/>.
/// </summary>
public sealed class AppSettings
{
    private static readonly byte[] Entropy = "ClickyWindows.v1"u8.ToArray();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClickyWindows");
    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    [JsonIgnore] public AppConfig Config { get; private set; } = new();

    // ── Secrets (decrypted in memory, never serialized as plain text) ───────
    [JsonIgnore] public string AnthropicApiKey { get; set; } = "";
    [JsonIgnore] public string AssemblyAiApiKey { get; set; } = "";
    [JsonIgnore] public string ElevenLabsApiKey { get; set; } = "";

    /// <summary>Name → base64 DPAPI blob. Only this form of the keys reaches disk.</summary>
    public Dictionary<string, string> ProtectedKeys { get; set; } = new();

    // ── User choices (empty = use the shipped default) ──────────────────────
    /// <summary>WASAPI endpoint id of the microphone; empty = Windows default.</summary>
    public string MicDeviceId { get; set; } = "";
    /// <summary>"Auto" (ElevenLabs if a key is set, else Windows), "ElevenLabs" or "Windows".</summary>
    public string Voice { get; set; } = "Auto";
    public string ElevenLabsVoiceId { get; set; } = "";
    /// <summary>Installed Windows voice name; empty = pick by the reply's language.</summary>
    public string WindowsVoice { get; set; } = "";
    public string PrimaryModel { get; set; } = "";
    /// <summary>Empty = shipped default; "none" disables escalation.</summary>
    public string EscalationModel { get; set; } = "";
    /// <summary>"Auto" (follow the language you speak / Windows), "es" or "en".</summary>
    public string Language { get; set; } = "Auto";
    /// <summary>Push-to-talk shortcut, e.g. "Ctrl+Shift+Space"; empty = appsettings.json default.</summary>
    public string Hotkey { get; set; } = "";
    /// <summary>Hold to dictate into the focused text box; empty = default, "Off" disables.</summary>
    public string DictateHotkey { get; set; } = "";
    /// <summary>Hold to say what to write (uses the screen and the focused box); empty = default, "Off" disables.</summary>
    public string WriteHotkey { get; set; } = "";
    /// <summary>Prompt mode: speech rewritten into a concise AI prompt. Empty = default; "Off" disables it.</summary>
    public string PromptHotkey { get; set; } = "";
    /// <summary>false = the blue cursor shows only while listening, thinking, speaking or pointing.</summary>
    public bool AlwaysShowCursor { get; set; }

    /// <summary>Dictation: punctuation, fillers and self-corrections fixed by the everyday model.</summary>
    public bool DictationCleanup { get; set; } = true;
    /// <summary>Dictation: words you fix right after dictating are added to <see cref="Dictionary"/>.</summary>
    public bool LearnWords { get; set; } = true;
    /// <summary>Names and terms to spell exactly (sent to speech-to-text and the cleanup model).</summary>
    public List<string> Dictionary { get; set; } = [];

    /// <summary>Copies each spoken answer to the clipboard.</summary>
    public bool CopyAnswers { get; set; }
    /// <summary>0.7–1.2 (ElevenLabs range); the Windows voice rate is mapped from it.</summary>
    public double VoiceSpeed { get; set; } = 1.0;
    /// <summary>After an answer, keep watching the screen and guide the next step.</summary>
    public bool TutorMode { get; set; }
    /// <summary>Computer use model; empty = appsettings.json Agent.Model (Opus 5.5).</summary>
    public string AgentModel { get; set; } = "";
    /// <summary>Skip the Allow card for new tasks (the countdown and irreversible-step questions still apply).</summary>
    public bool AgentAlwaysAllow { get; set; }
    /// <summary>USD per task; 0 = appsettings.json Agent.CostCap.</summary>
    public double AgentCostCap { get; set; }
    /// <summary>Talk less around tasks: no spoken line before acting, one word after (answers and problems are still spoken).</summary>
    public bool AgentQuiet { get; set; } = true;
    /// <summary>Facts Clicky saved with its remember tool (which Chrome profile to use, …); sent with every task.</summary>
    public List<string> AgentNotes { get; set; } = [];

    // ── Effective values ────────────────────────────────────────────────────
    [JsonIgnore] public string EffectiveAgentModel =>
        string.IsNullOrWhiteSpace(AgentModel) ? Config.Agent.Model : AgentModel;
    [JsonIgnore] public double EffectiveAgentCostCap => AgentCostCap > 0 ? AgentCostCap : Config.Agent.CostCap;
    [JsonIgnore] public string EffectivePrimaryModel =>
        string.IsNullOrWhiteSpace(PrimaryModel) ? Config.Models.Primary : PrimaryModel;

    [JsonIgnore] public string? EffectiveEscalationModel
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(EscalationModel) ? Config.Models.Escalation : EscalationModel;
            return string.IsNullOrWhiteSpace(model) || model.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                   model == EffectivePrimaryModel ? null : model;
        }
    }

    [JsonIgnore] public string EffectiveHotkey =>
        string.IsNullOrWhiteSpace(Hotkey) ? Config.Hotkey : Hotkey;
    [JsonIgnore] public string EffectiveDictateHotkey =>
        string.IsNullOrWhiteSpace(DictateHotkey) ? Config.DictateHotkey : DictateHotkey;
    [JsonIgnore] public string EffectiveWriteHotkey =>
        string.IsNullOrWhiteSpace(WriteHotkey) ? Config.WriteHotkey : WriteHotkey;
    [JsonIgnore] public string EffectivePromptHotkey =>
        string.IsNullOrWhiteSpace(PromptHotkey) ? Config.PromptHotkey : PromptHotkey;

    [JsonIgnore] public double EffectiveVoiceSpeed => Math.Clamp(VoiceSpeed is > 0 ? VoiceSpeed : 1.0, 0.7, 1.2);

    [JsonIgnore] public string EffectiveElevenLabsVoiceId =>
        string.IsNullOrWhiteSpace(ElevenLabsVoiceId) ? Config.Speech.ElevenLabsVoiceId : ElevenLabsVoiceId;

    [JsonIgnore] public bool UseElevenLabs => Voice switch
    {
        "Windows" => false,
        "ElevenLabs" => true,
        _ => !string.IsNullOrWhiteSpace(ElevenLabsApiKey),
    };

    [JsonIgnore] public bool HasRequiredKeys =>
        !string.IsNullOrWhiteSpace(AnthropicApiKey) && !string.IsNullOrWhiteSpace(AssemblyAiApiKey);

    // ── Persistence ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load()
    {
        var config = AppConfig.Load();
        AppSettings settings;
        string? json = null;
        try
        {
            json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            settings = json != null
                ? JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] settings.json is invalid ({ex.Message}) — starting fresh");
            settings = new AppSettings();
        }

        settings.Config = config;
        settings.AnthropicApiKey = settings.Unprotect(nameof(AnthropicApiKey));
        settings.AssemblyAiApiKey = settings.Unprotect(nameof(AssemblyAiApiKey));
        settings.ElevenLabsApiKey = settings.Unprotect(nameof(ElevenLabsApiKey));
        settings.MigrateLegacy(json);
        return settings;
    }

    /// <summary>Encrypts the keys and writes the file atomically (temp file + replace).</summary>
    public void Save()
    {
        ProtectedKeys = new Dictionary<string, string>();
        Protect(nameof(AnthropicApiKey), AnthropicApiKey);
        Protect(nameof(AssemblyAiApiKey), AssemblyAiApiKey);
        Protect(nameof(ElevenLabsApiKey), ElevenLabsApiKey);

        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(temp, FilePath, overwrite: true);
        Logger.Log("[Settings] Saved");
    }

    private void Protect(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser);
        ProtectedKeys[name] = Convert.ToBase64String(blob);
    }

    private string Unprotect(string name)
    {
        if (!ProtectedKeys.TryGetValue(name, out var b64) || string.IsNullOrEmpty(b64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] Could not decrypt {name} (saved by another Windows user?): {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Older versions stored keys in plain text: adopt them and rewrite the file encrypted
    /// (unknown legacy fields are dropped by the rewrite).
    /// </summary>
    private void MigrateLegacy(string? json)
    {
        if (json == null) return;
        System.Text.Json.Nodes.JsonObject? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject; }
        catch { return; }
        if (root == null) return;

        bool migrated = false;
        foreach (var name in new[] { nameof(AnthropicApiKey), nameof(AssemblyAiApiKey), nameof(ElevenLabsApiKey) })
        {
            if (root[name] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue(out string? value) ||
                string.IsNullOrWhiteSpace(value))
                continue;
            var property = GetType().GetProperty(name)!;
            if (string.IsNullOrWhiteSpace((string?)property.GetValue(this)))
                property.SetValue(this, value);
            migrated = true; // rewrite even if an encrypted copy exists, to remove the plain text
        }
        if (!migrated) return;
        try
        {
            Save();
            Logger.Log("[Settings] Moved plain-text API keys to encrypted storage");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] Could not rewrite settings.json: {ex.Message}");
        }
    }
}

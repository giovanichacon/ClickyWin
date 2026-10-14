using System.Text.Json;
using ClickyWindows.Helpers;

namespace ClickyWindows.Settings;

/// <summary>
/// Shipped defaults from appsettings.json next to the exe: prompts, hotkey, endpoints, models.
/// Edit that file to customize them. Per-user choices (keys, mic, voice) live in
/// <see cref="AppSettings"/> instead. Missing values fall back to the defaults below.
/// </summary>
public sealed class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";
    public bool MicWarmUp { get; set; } = true;
    public ModelConfig Models { get; set; } = new();
    public PromptConfig Prompts { get; set; } = new();
    public EndpointConfig Endpoints { get; set; } = new();
    public SpeechConfig Speech { get; set; } = new();
    public ScreenshotConfig Screenshots { get; set; } = new();

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts) ?? new AppConfig();
            Logger.Log($"[Config] {FilePath} not found — using built-in defaults");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Config] appsettings.json is invalid ({ex.Message}) — using built-in defaults");
        }
        return new AppConfig();
    }
}

public sealed class ModelConfig
{
    public string Primary { get; set; } = "claude-haiku-5-5";
    public string PrimaryEffort { get; set; } = "low";
    public int PrimaryMaxTokens { get; set; } = 2048;
    /// <summary>Model that answers when the primary replies [ESCALATE]. Empty disables escalation.</summary>
    public string Escalation { get; set; } = "claude-opus-5-5";
    public string EscalationEffort { get; set; } = "medium";
    public int EscalationMaxTokens { get; set; } = 16000;
    public int ConversationTurns { get; set; } = 10;
}

public sealed class PromptConfig
{
    // Arrays of lines so the prompts stay readable in JSON
    public string[] System { get; set; } =
    [
        "You are Clicky, a friendly assistant that lives next to the user's cursor on Windows and can see their screen.",
        "Reply briefly in the language the user spoke, as plain spoken text.",
        "To show where something is, end with [POINT:x,y:label] using the screenshot's pixel coordinates.",
    ];
    public string[] EscalationRule { get; set; } =
    [
        "If a good reply needs careful multi-step reasoning or you are not confident, reply with exactly [ESCALATE] and nothing else.",
    ];

    public string SystemText => string.Join("\n", System);
    public string EscalationText => string.Join("\n", EscalationRule);
}

public sealed class EndpointConfig
{
    public string Anthropic { get; set; } = "https://api.anthropic.com";
    public string AssemblyAiStreaming { get; set; } = "wss://streaming.assemblyai.com/v3/ws";
    public string ElevenLabs { get; set; } = "https://api.elevenlabs.io";
}

public sealed class SpeechConfig
{
    public string AssemblyAiModel { get; set; } = "universal-3-6-pro";
    public string ElevenLabsModel { get; set; } = "eleven_flash_v2_5";
    public string ElevenLabsVoiceId { get; set; } = "EXAVITQu4vr4xnSDxMaL";
}

public sealed class ScreenshotConfig
{
    /// <summary>Claude downsizes images beyond these limits; staying under them keeps pointing coordinates exact.</summary>
    public int MaxLongEdge { get; set; } = 1568;
    public double MaxMegapixels { get; set; } = 1.15;
    public int JpegQuality { get; set; } = 80;
}

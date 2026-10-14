using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public record PointTarget(int X, int Y, string Label, int Screen);

/// <summary>What happened while producing one reply (filled in as the stream runs).</summary>
public sealed class ReplyInfo
{
    public string Model { get; set; } = "";
    public bool Escalated { get; set; }
    public bool Refused { get; set; }
}

/// <summary>
/// Streams replies from Claude through the official Anthropic SDK.
///
/// Routing: the everyday model (Haiku by default, thinking off, low effort) answers first.
/// Its system prompt lets it reply with exactly [ESCALATE] when a question needs deeper
/// reasoning; the first characters are held back until that is ruled out, and on [ESCALATE]
/// the same request goes to the stronger model (Opus by default) instead.
///
/// Parses [POINT:x,y:label(:screenN)] tags (screenshot pixel coordinates) for the overlay
/// and keeps a rolling text history for follow-up questions.
/// </summary>
public sealed partial class ClaudeService(AppSettings settings, ConversationHistory history)
{
    private const string EscalateTag = "[ESCALATE]";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    // Models that accept server-side refusal fallbacks ("default" routing by refusal category)
    private static readonly HashSet<string> FallbackModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-1"];

    // USD per million input/output tokens, for the cost line in the log
    private static readonly Dictionary<string, (double input, double output)> Prices = new()
    {
        ["claude-haiku-5-5"] = (0.10, 0.50),
        ["claude-sonnet-5-5"] = (2.00, 10.00),
        ["claude-opus-5-5"] = (4.00, 20.00),
    };

    [GeneratedRegex(@"\[POINT:(\d+),(\d+):([^\]:]*)(?::screen(\d+))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex PointTagRegex();

    [GeneratedRegex(@"\s*\[POINT:[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex AnyPointTag();

    private readonly object _clientLock = new();
    private AnthropicClient? _client;
    private string _clientKey = "";
    private string _clientUrl = "";

    /// <summary>
    /// Sends transcript + screenshots (with the conversation so far) and yields reply text
    /// chunks as they arrive. <paramref name="info"/> reports the model used, escalation and refusals.
    /// </summary>
    public async IAsyncEnumerable<string> StreamReplyAsync(
        string transcript,
        IReadOnlyList<ScreenshotResult> screenshots,
        ReplyInfo info,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = HistoryMessages(history.Turns);
        messages.Add(new BetaMessageParam { Role = Role.User, Content = BuildUserContent(transcript, screenshots) });

        var reply = new StringBuilder();
        await foreach (var chunk in StreamRoutedAsync(settings.Config.Prompts.SystemText, messages, info, ct).ConfigureAwait(false))
        {
            reply.Append(chunk);
            yield return chunk;
        }

        if (reply.Length > 0)
        {
            history.AddUserMessage(transcript);
            history.AddAssistantMessage(StripPointTags(reply.ToString()));
        }
    }

    /// <summary>
    /// One-shot request with its own system prompt (dictation cleanup, Write, tutor checks),
    /// routed like a spoken question: everyday model first, [ESCALATE] when allowed.
    /// </summary>
    public async Task<string> CompleteAsync(string system, List<BetaContentBlockParam> content, ReplyInfo info,
        bool allowEscalation, CancellationToken ct, IReadOnlyList<ConversationTurn>? turns = null)
    {
        var messages = HistoryMessages(turns ?? []);
        messages.Add(new BetaMessageParam { Role = Role.User, Content = content });

        var reply = new StringBuilder();
        var stream = allowEscalation
            ? StreamRoutedAsync(system, messages, info, ct)
            : StreamModelAsync(settings.EffectivePrimaryModel, system, messages, info, ct);
        await foreach (var chunk in stream.ConfigureAwait(false))
            reply.Append(chunk);
        return reply.ToString();
    }

    private static List<BetaMessageParam> HistoryMessages(IEnumerable<ConversationTurn> turns) =>
        turns.Select(t => new BetaMessageParam { Role = t.Role == "user" ? Role.User : Role.Assistant, Content = t.Content })
             .ToList();

    /// <summary>Everyday model first; on a leading [ESCALATE] the same request goes to the stronger model.</summary>
    private async IAsyncEnumerable<string> StreamRoutedAsync(
        string system,
        List<BetaMessageParam> messages,
        ReplyInfo info,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var primary = settings.EffectivePrimaryModel;
        var escalation = settings.EffectiveEscalationModel;

        if (escalation == null)
        {
            await foreach (var chunk in StreamModelAsync(primary, system, messages, info, ct).ConfigureAwait(false))
                yield return chunk;
            yield break;
        }

        // Hold back the start of the reply until it can't be the escalation tag
        var head = new StringBuilder();
        bool decided = false, escalate = false;
        var primarySystem = system + "\n\n" + settings.Config.Prompts.EscalationText;

        await foreach (var chunk in StreamModelAsync(primary, primarySystem, messages, info, ct).ConfigureAwait(false))
        {
            if (decided)
            {
                yield return chunk;
                continue;
            }

            head.Append(chunk);
            var start = head.ToString().TrimStart();
            if (start.Length < EscalateTag.Length && EscalateTag.StartsWith(start, StringComparison.OrdinalIgnoreCase))
                continue; // could still become [ESCALATE]

            decided = true;
            if (start.StartsWith(EscalateTag, StringComparison.OrdinalIgnoreCase))
            {
                escalate = true;
                break; // abandons the primary stream (the HTTP response is closed)
            }
            yield return head.ToString();
        }

        if (!decided)
        {
            var start = head.ToString().Trim();
            if (start.StartsWith("[ESCALATE", StringComparison.OrdinalIgnoreCase))
                escalate = true;
            else if (start.Length > 0)
                yield return head.ToString();
        }

        if (escalate)
        {
            info.Escalated = true;
            Logger.Log($"[Claude] {primary} escalated the request to {escalation}");
            await foreach (var chunk in StreamModelAsync(escalation, system, messages, info, ct).ConfigureAwait(false))
                yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> StreamModelAsync(
        string model,
        string system,
        List<BetaMessageParam> messages,
        ReplyInfo info,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = settings.Config;
        bool isEscalation = model == settings.EffectiveEscalationModel;

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = isEscalation ? cfg.Models.EscalationMaxTokens : cfg.Models.PrimaryMaxTokens,
            System = system,
            Messages = messages,
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(isEscalation ? cfg.Models.EscalationEffort : cfg.Models.PrimaryEffort),
            },
        };

        // Haiku 5.5 may run with thinking off (fastest spoken replies); Opus/Sonnet 5.5
        // reject disabled thinking, so they run adaptive and are steered by effort.
        if (model.StartsWith("claude-haiku-5", StringComparison.Ordinal))
            parameters = parameters with { Thinking = new BetaThinkingConfigDisabled() };

        // A safety-classifier refusal is re-served by a fallback model inside the same call
        if (FallbackModels.Contains(model))
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new Default() };

        info.Model = model;
        long inputTokens = 0, outputTokens = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        double firstTextMs = -1;

        try
        {
            await foreach (var ev in Client().Beta.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
            {
                if (ev.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    if (firstTextMs < 0)
                        firstTextMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    yield return text.Text;
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    outputTokens = messageDelta.Usage.OutputTokens;
                    if (messageDelta.Delta.StopReason == BetaStopReason.Refusal)
                    {
                        info.Refused = true;
                        Logger.Log($"[Claude] {model} declined the request");
                    }
                }
            }
        }
        finally
        {
            var cost = Prices.TryGetValue(model, out var p)
                ? $" ≈ ${(inputTokens * p.input + outputTokens * p.output) / 1_000_000:F5}"
                : "";
            Logger.Log($"[Claude] {model}: first text {firstTextMs:F0} ms, " +
                       $"{inputTokens} in / {outputTokens} out tokens{cost}");
        }
    }

    public static List<BetaContentBlockParam> BuildUserContent(string transcript, IReadOnlyList<ScreenshotResult> screenshots)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var shot in screenshots)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = shot.Base64, MediaType = "image/jpeg" },
            });
            content.Add(new BetaTextBlockParam { Text = shot.Label });
        }
        content.Add(new BetaTextBlockParam { Text = transcript });
        return content;
    }

    private static string ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" or "medium" or "high" or "xhigh" or "max" => effort.ToLowerInvariant(),
        _ => "medium",
    };

    private AnthropicClient Client()
    {
        lock (_clientLock)
        {
            var key = settings.AnthropicApiKey;
            var url = settings.Config.Endpoints.Anthropic;
            if (_client == null || key != _clientKey || url != _clientUrl)
            {
                _client = new AnthropicClient { ApiKey = key, BaseUrl = url };
                _clientKey = key;
                _clientUrl = url;
            }
            return _client;
        }
    }

    /// <summary>Checks a key by looking up the everyday model (free, no tokens used).</summary>
    public static async Task<(bool ok, string message)> TestKeyAsync(string key, string baseUrl, string model)
    {
        try
        {
            var client = new AnthropicClient { ApiKey = key.Trim(), BaseUrl = baseUrl };
            var info = await client.Models.Retrieve(model).ConfigureAwait(false);
            return (true, info.DisplayName);
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>Parses [POINT:x,y:label] and [POINT:x,y:label:screenN] tags.</summary>
    public static List<PointTarget> ParsePoints(string text)
    {
        var points = new List<PointTarget>();
        foreach (Match m in PointTagRegex().Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out int x) && int.TryParse(m.Groups[2].Value, out int y))
            {
                int screen = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int s) ? s : 1;
                points.Add(new PointTarget(x, y, m.Groups[3].Value.Trim(), screen));
            }
        }
        return points;
    }

    /// <summary>Removes [POINT:...] tags (including [POINT:none]) before speaking or storing text.</summary>
    public static string StripPointTags(string text) => AnyPointTag().Replace(text, "").Trim();
}

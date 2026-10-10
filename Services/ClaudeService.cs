using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public record ClaudeResponse(string Text, List<PointTarget> Points);
public record PointTarget(int X, int Y, string Label);

/// <summary>
/// Streams responses from Claude (claude-sonnet-4-6) via Server-Sent Events.
/// Parses [POINT:x,y:label] tags from the response for overlay positioning.
/// Maintains conversation history for context across turns.
/// </summary>
public partial class ClaudeService
{
    private static readonly HttpClient Http = new();

    // Match [POINT:123,456:element_name] tags in Claude's response
    [GeneratedRegex(@"\[POINT:(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex PointTagRegex();

    private static readonly string SystemPrompt = """
        You are Clicky, a helpful AI assistant that can see the user's screen.
        You help users understand what they're looking at and answer questions about on-screen content.

        POINT tags — use ONLY when the user explicitly asks you to locate, show, find, or point to
        a specific element on screen (e.g. "where is the save button?", "point to the menu").
        Do NOT include POINT tags for general questions, greetings, or answers that don't require
        highlighting a specific location.

        When a POINT tag IS appropriate, format it as [POINT:x,y:label] where x,y are the screen
        coordinates in pixels of the element center, and label is a short description (2-4 words).

        Keep all responses concise and conversational — responses are spoken aloud via text-to-speech.
        Do not use markdown, bullet points, numbered lists, or code blocks.
        Speak naturally as if talking to someone sitting next to you.
        Speak in Turkish all the time.
        """;

    private readonly AppSettings _settings;
    private readonly ConversationHistory _history;

    public ClaudeService(AppSettings settings, ConversationHistory history)
    {
        _settings = settings;
        _history = history;

        // Pre-configure headers
        Http.DefaultRequestHeaders.Remove("x-api-key");
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            Http.DefaultRequestHeaders.Add("x-api-key", settings.AnthropicApiKey);
        Http.DefaultRequestHeaders.Remove("anthropic-version");
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    /// <summary>
    /// Sends transcript + screenshots to Claude and streams back the response.
    /// Yields text chunks as they arrive.
    /// </summary>
    public async IAsyncEnumerable<string> StreamResponseAsync(
        string transcript,
        List<Services.ScreenshotResult> screenshots,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build the content array: images first, then text
        var contentArray = new JsonArray();

        foreach (var shot in screenshots)
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = shot.Base64,
                },
            });
            contentArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = shot.Label,
            });
        }

        contentArray.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = transcript,
        });

        // Build messages array with conversation history
        var messages = new JsonArray();
        foreach (var turn in _history.Turns)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Content,
            });
        }
        // Add current user message (text only for history, images are in current message)
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = contentArray,
        });

        var body = new JsonObject
        {
            ["model"] = _settings.ClaudeModel,
            ["max_tokens"] = 1024,
            ["stream"] = true,
            ["system"] = SystemPrompt,
            ["messages"] = messages,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Claude API request failed: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Claude API error {(int)response.StatusCode}: {errorBody}");
        }

        var fullResponse = new StringBuilder();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        // ReadLineAsync returns null at end of stream; EndOfStream would block synchronously
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            string? chunk = null;
            try
            {
                var node = JsonNode.Parse(data);
                if (node?["type"]?.GetValue<string>() == "content_block_delta")
                    chunk = node["delta"]?["text"]?.GetValue<string>();
            }
            catch { continue; }

            if (!string.IsNullOrEmpty(chunk))
            {
                fullResponse.Append(chunk);
                yield return chunk;
            }
        }

        // Save to conversation history (strip POINT tags for cleaner history)
        var responseText = fullResponse.ToString();
        var cleanText = PointTagRegex().Replace(responseText, "").Trim();
        _history.AddUserMessage(transcript);
        _history.AddAssistantMessage(cleanText);
    }

    /// <summary>
    /// Parses [POINT:x,y:label] tags from a complete response string.
    /// </summary>
    public static List<PointTarget> ParsePoints(string responseText)
    {
        var points = new List<PointTarget>();
        foreach (Match m in PointTagRegex().Matches(responseText))
        {
            if (int.TryParse(m.Groups[1].Value, out int x) &&
                int.TryParse(m.Groups[2].Value, out int y))
            {
                points.Add(new PointTarget(x, y, m.Groups[3].Value));
            }
        }
        return points;
    }

    /// <summary>
    /// Strips [POINT:...] tags from text before sending to TTS.
    /// </summary>
    public static string StripPointTags(string text) =>
        PointTagRegex().Replace(text, "").Trim();

    /// <summary>
    /// Uses Claude's Computer Use API to precisely locate a named UI element on screen.
    /// Sends a resized screenshot (at CU resolution) and gets back physical pixel coordinates.
    /// Returns (-1, -1) if the element cannot be found.
    /// </summary>
    public async Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg,
        string elementDescription,
        int screenWidth, int screenHeight,
        CancellationToken ct = default)
    {
        var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(screenWidth, screenHeight);

        var body = new JsonObject
        {
            ["model"] = _settings.ClaudeModel,
            ["max_tokens"] = 256,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "computer_20251124",
                    ["name"] = "computer",
                    ["display_width_px"] = cuW,
                    ["display_height_px"] = cuH,
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = base64Jpeg,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Locate the '{elementDescription}' element on this screen and move the mouse to it.",
                        },
                    },
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("anthropic-beta", "computer-use-2025-11-24");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Computer Use API request failed: {ex.Message}", ex);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Computer Use API error {(int)response.StatusCode}: {json}");

        var node = JsonNode.Parse(json);
        var content = node?["content"]?.AsArray();
        if (content != null)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() == "tool_use" &&
                    block?["name"]?.GetValue<string>() == "computer")
                {
                    var coord = block?["input"]?["coordinate"]?.AsArray();
                    if (coord?.Count >= 2)
                    {
                        int cuX = coord[0]!.GetValue<int>();
                        int cuY = coord[1]!.GetValue<int>();
                        // Scale from CU space to physical pixels
                        int physX = (int)((double)cuX / cuW * screenWidth);
                        int physY = (int)((double)cuY / cuH * screenHeight);
                        return (physX, physY);
                    }
                }
            }
        }

        return (-1, -1);
    }
}

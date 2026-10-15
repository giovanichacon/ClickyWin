using System.Drawing;
using System.Net.Http;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public enum AgentOutcome { Done, Stopped, Failed, LimitReached }

public sealed record AgentResult(AgentOutcome Outcome, string Summary, double Cost, int Actions);

/// <summary>
/// Computer use: Claude operates the primary monitor with the computer toolset
/// (computer_toolset_20260801: screenshot, zoom, clicks, drag, scroll, type, key, ...) plus an
/// ask_user tool it must call before anything irreversible. Screenshots are scaled down to about
/// 1280 px wide; Claude's coordinates are in that image space and are mapped back to physical
/// pixels here. Batches run in order and halt at the first failure. Limits: actions, turns and a
/// per-task cost cap. Run off the UI thread; cancel the token to stop at once.
/// </summary>
public sealed class ComputerAgent(AppSettings settings)
{
    private const string ToolsetName = "computer";
    private const string AskUserTool = "ask_user";
    private const string HaltText = "Not executed: an earlier computer action in this turn failed.";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private static readonly HashSet<string> FallbackModels = ["claude-opus-5-5", "claude-sonnet-5-5"];

    // USD per million tokens: input, output, cache read
    private static readonly Dictionary<string, (double input, double output, double cacheRead)> Prices = new()
    {
        ["claude-haiku-5-5"] = (0.10, 0.50, 0.01),
        ["claude-sonnet-5-5"] = (2.00, 10.00, 0.20),
        ["claude-opus-5-5"] = (4.00, 20.00, 0.20),
    };

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Short text for the "working" banner: what Clicky is doing now.</summary>
    public event Action<string>? Progress;

    /// <summary>Asks the user a yes/no question (irreversible step, or a secret they must type). True = yes.</summary>
    public Func<string, CancellationToken, Task<bool>>? AskUser { get; set; }

    /// <summary>Fires with the physical point Clicky is about to click (the overlay can show it).</summary>
    public event Action<int, int>? Acting;

    private Rectangle _bounds;      // physical bounds of the controlled screen
    private double _scale = 1;      // image px per physical px
    private int _width, _height;    // screenshot size sent to Claude

    public async Task<AgentResult> RunAsync(string task, CancellationToken ct)
    {
        var cfg = settings.Config.Agent;
        var model = settings.EffectiveAgentModel;
        var client = new AnthropicClient
        {
            ApiKey = settings.AnthropicApiKey,
            BaseUrl = settings.Config.Endpoints.Anthropic,
            Handlers = [new ErrorBodyLogger()],
        };
        double cost = 0;
        int actions = 0;

        Logger.Log($"[Agent] Start ({model}): \"{task}\"");
        var first = Capture();
        var messages = new List<BetaMessageParam>
        {
            new()
            {
                Role = Role.User,
                Content = new List<BetaContentBlockParam>
                {
                    new BetaTextBlockParam { Text = $"Task: {task}\n\nThe current screen ({_width}x{_height} pixels):" },
                    Image(first),
                },
            },
        };

        try
        {
            for (int turn = 0; turn < cfg.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = cfg.MaxTokens,
                    System = string.Join("\n", settings.Config.Prompts.Agent) + $"\nReply in this language: {L.CurrentLanguageName}.",
                    Messages = messages,
                    Tools = Tools(),
                    OutputConfig = new BetaOutputConfig { Effort = cfg.Effort },
                    CacheControl = new BetaCacheControlEphemeral(),
                };
                if (FallbackModels.Contains(model))
                    parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new Default() };

                var response = await client.Beta.Messages.Create(parameters, ct).ConfigureAwait(false);
                cost += Cost(model, response.Usage);

                var assistant = new List<BetaContentBlockParam>();
                var calls = new List<BetaToolUseBlock>();
                var said = new StringBuilder();
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistant.Add(new BetaTextBlockParam { Text = text.Text });
                        said.Append(text.Text);
                    }
                    else if (block.TryPickThinking(out var thinking))
                        assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    else if (block.TryPickRedactedThinking(out var redacted))
                        assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    else if (block.TryPickToolUse(out var use))
                    {
                        assistant.Add(new BetaToolUseBlockParam
                        {
                            ID = use.ID, Name = use.Name, Input = use.Input, ToolsetName = use.ToolsetName,
                        });
                        calls.Add(use);
                    }
                }
                messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });

                var note = said.ToString().Trim();
                if (note.Length > 0) Logger.Log($"[Agent] Claude: \"{note}\"");

                if (response.StopReason == BetaStopReason.Refusal)
                    return Finish(AgentOutcome.Failed, L.AgentRefused, cost, actions);
                if (calls.Count == 0)
                    return Finish(AgentOutcome.Done, note.Length > 0 ? note : L.AgentDone, cost, actions);

                if (note.Length > 0) Progress?.Invoke(note);

                var results = new List<BetaContentBlockParam>();
                bool halted = false;
                foreach (var call in calls)
                {
                    if (halted)
                    {
                        results.Add(Result(call, HaltText, isError: true));
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (call.Name != AskUserTool && ++actions > cfg.MaxActions)
                        return Finish(AgentOutcome.LimitReached, L.AgentLimit, cost, actions - 1);

                    BetaToolResultBlockParam result;
                    try
                    {
                        result = await ExecuteAsync(call, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Agent] {call.Name} failed: {ex.Message}");
                        result = Result(call, $"Error: {ex.Message}", isError: true);
                    }
                    if (result.IsError == true) halted = true;
                    results.Add(result);
                }
                messages.Add(new BetaMessageParam { Role = Role.User, Content = results });

                if (cost > settings.EffectiveAgentCostCap)
                    return Finish(AgentOutcome.LimitReached, L.AgentCostLimit, cost, actions);
            }
            return Finish(AgentOutcome.LimitReached, L.AgentLimit, cost, actions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Finish(AgentOutcome.Stopped, L.AgentStopped, cost, actions);
        }
        catch (Exception ex)
        {
            // API errors keep the server's message in a body property, not in Message
            var detail = ex.Message + string.Concat(ex.GetType().GetProperties()
                .Where(p => p.Name.Contains("Body", StringComparison.Ordinal))
                .Select(p => $" | {p.Name}: {p.GetValue(ex)}"));
            Logger.Error($"[Agent] Failed: {detail[..Math.Min(detail.Length, 1500)]}");
            return Finish(AgentOutcome.Failed, L.AgentFailed, cost, actions);
        }
    }

    private static AgentResult Finish(AgentOutcome outcome, string summary, double cost, int actions)
    {
        Logger.Log($"[Agent] {outcome} after {actions} action(s), ≈ ${cost:F4}: \"{summary}\"");
        return new AgentResult(outcome, summary, cost, actions);
    }

    private static List<BetaToolUnion> Tools() =>
    [
        new BetaComputerToolset20260801(),
        new BetaTool
        {
            Name = AskUserTool,
            Description = "Ask the user a yes/no question and wait for the answer. Required before anything " +
                          "irreversible or outward-facing (send, post, publish, delete, buy, pay, submit a form, " +
                          "install, change account or security settings), and when the user must type a password, " +
                          "payment details or a code themselves. The user answers by clicking Yes or No.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["question"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "One short question in the user's language, saying exactly what will happen.",
                    }),
                },
                Required = ["question"],
            },
        },
    ];

    // ── Actions ─────────────────────────────────────────────────────────────

    private async Task<BetaToolResultBlockParam> ExecuteAsync(BetaToolUseBlock call, CancellationToken ct)
    {
        var input = call.Input;
        Logger.Log($"[Agent] {call.Name} {JsonSerializer.Serialize(input)}");

        if (call.Name == AskUserTool)
        {
            string question = Str(input, "question") ?? "";
            bool yes = AskUser != null && await AskUser(question, ct).ConfigureAwait(false);
            Logger.Log($"[Agent] Asked \"{question}\" → {(yes ? "yes" : "no")}");
            return Result(call, yes ? "The user said yes. Go ahead."
                                    : "The user said no. Do not do it. Stop and say briefly what you did not do.");
        }

        Progress?.Invoke(L.AgentStep(call.Name));
        string modifiers = Str(input, "text") ?? "";
        bool ok = true;
        switch (call.Name)
        {
            case "screenshot":
                return ImageResult(call, Capture());

            case "zoom":
                var r = Coords(input, "region", 4) ?? throw new ArgumentException("region [x0, y0, x1, y1] is required");
                return ImageResult(call, CaptureZoom(r));

            case "left_click" or "right_click" or "middle_click" or "double_click" or "triple_click":
                if (Point(input, "coordinate") is { } at)
                {
                    Acting?.Invoke(at.x, at.y);
                    ok = InputDriver.MoveTo(at.x, at.y);
                    await Task.Delay(40, ct).ConfigureAwait(false);
                }
                var button = call.Name switch
                {
                    "right_click" => InputDriver.Button.Right,
                    "middle_click" => InputDriver.Button.Middle,
                    _ => InputDriver.Button.Left,
                };
                int count = call.Name switch { "double_click" => 2, "triple_click" => 3, _ => 1 };
                ok = ok && InputDriver.Click(button, count, modifiers);
                break;

            case "left_click_drag":
                var from = Point(input, "start_coordinate") ?? throw new ArgumentException("start_coordinate is required");
                var to = Point(input, "coordinate") ?? throw new ArgumentException("coordinate is required");
                Acting?.Invoke(to.x, to.y);
                ok = await InputDriver.DragAsync(from.x, from.y, to.x, to.y, modifiers, ct).ConfigureAwait(false);
                break;

            case "mouse_move":
                var move = Point(input, "coordinate") ?? throw new ArgumentException("coordinate is required");
                ok = InputDriver.MoveTo(move.x, move.y);
                break;

            case "left_mouse_down":
                ok = InputDriver.ButtonDown(InputDriver.Button.Left);
                break;

            case "left_mouse_up":
                ok = InputDriver.ButtonUp(InputDriver.Button.Left);
                break;

            case "cursor_position":
                Win32.GetCursorPos(out var cursor);
                int cx = (int)Math.Round((cursor.X - _bounds.Left) * _scale);
                int cy = (int)Math.Round((cursor.Y - _bounds.Top) * _scale);
                return Result(call, $"[{cx}, {cy}]");

            case "scroll":
                if (Point(input, "coordinate") is { } over) ok = InputDriver.MoveTo(over.x, over.y);
                ok = ok && InputDriver.Scroll(Str(input, "scroll_direction") ?? "down", Int(input, "scroll_amount") ?? 3, modifiers);
                break;

            case "type":
                ok = await InputDriver.TypeAsync(Str(input, "text") ?? "", ct).ConfigureAwait(false);
                break;

            case "key":
                ok = InputDriver.Press(Str(input, "text") ?? "", Int(input, "repeat") ?? 1);
                break;

            case "hold_key":
                ok = await InputDriver.HoldAsync(Str(input, "text") ?? "", Dbl(input, "duration") ?? 1, ct).ConfigureAwait(false);
                break;

            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Dbl(input, "duration") ?? 1, 0, 30)), ct).ConfigureAwait(false);
                return Result(call, "OK");

            default:
                return Result(call, $"Unknown action \"{call.Name}\"", isError: true);
        }

        if (!ok)
            return Result(call, "Windows blocked the input (an administrator window may be in front).", isError: true);
        await Task.Delay(settings.Config.Agent.SettleMs, ct).ConfigureAwait(false); // let the UI react
        return Result(call, "OK");
    }

    private static BetaToolResultBlockParam Result(BetaToolUseBlock call, string text, bool isError = false)
    {
        var result = new BetaToolResultBlockParam { ToolUseID = call.ID, ToolsetName = call.ToolsetName, Content = text };
        // Only set when true: an explicit null is sent as "is_error": null, which the API rejects
        return isError ? result with { IsError = true } : result;
    }

    private static BetaToolResultBlockParam ImageResult(BetaToolUseBlock call, string jpegBase64) => new()
    {
        ToolUseID = call.ID,
        ToolsetName = call.ToolsetName,
        Content = new List<Block> { Image(jpegBase64) },
    };

    private static BetaImageBlockParam Image(string jpegBase64) => new()
    {
        Source = new BetaBase64ImageSource { Data = jpegBase64, MediaType = "image/jpeg" },
    };

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>The primary monitor, scaled to fit the configured size; sets the coordinate space.</summary>
    private string Capture()
    {
        var cfg = settings.Config.Agent;
        _bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        _scale = Math.Min(1.0, Math.Min((double)cfg.ScreenshotWidth / _bounds.Width, (double)cfg.ScreenshotHeight / _bounds.Height));
        _width = Math.Max(1, (int)Math.Round(_bounds.Width * _scale));
        _height = Math.Max(1, (int)Math.Round(_bounds.Height * _scale));
        _scale = (double)_width / _bounds.Width;

        using var full = Grab(_bounds);
        return Encode(full, _width, _height);
    }

    /// <summary>A region (screenshot coordinates) at full resolution, scaled to fit the screenshot size.</summary>
    private string CaptureZoom(int[] region)
    {
        var (x0, y0) = ToPhysical(Math.Min(region[0], region[2]), Math.Min(region[1], region[3]));
        var (x1, y1) = ToPhysical(Math.Max(region[0], region[2]), Math.Max(region[1], region[3]));
        var rect = Rectangle.FromLTRB(x0, y0, Math.Max(x1, x0 + 8), Math.Max(y1, y0 + 8));
        rect.Intersect(_bounds);
        using var crop = Grab(rect);
        double fit = Math.Min((double)_width / rect.Width, (double)_height / rect.Height);
        return Encode(crop, Math.Max(1, (int)(rect.Width * fit)), Math.Max(1, (int)(rect.Height * fit)));
    }

    private static Bitmap Grab(Rectangle rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size);
        return bitmap;
    }

    private string Encode(Bitmap source, int width, int height)
    {
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }
        using var ms = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)settings.Config.Screenshots.JpegQuality);
        scaled.Save(ms, JpegCodec, parameters);
        return Convert.ToBase64String(ms.ToArray());
    }

    private (int x, int y) ToPhysical(double x, double y) =>
        ((int)Math.Round(_bounds.Left + Math.Clamp(x, 0, _width - 1) / _scale),
         (int)Math.Round(_bounds.Top + Math.Clamp(y, 0, _height - 1) / _scale));

    // ── Input parsing ───────────────────────────────────────────────────────

    private (int x, int y)? Point(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        Coords(input, key, 2) is { } c ? ToPhysical(c[0], c[1]) : null;

    private static int[]? Coords(IReadOnlyDictionary<string, JsonElement> input, string key, int count)
    {
        if (!input.TryGetValue(key, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != count)
            return null;
        return e.EnumerateArray().Select(v => (int)Math.Round(v.GetDouble())).ToArray();
    }

    private static string? Str(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : null;

    private static double? Dbl(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private static double Cost(string model, BetaUsage usage)
    {
        if (!Prices.TryGetValue(model, out var p)) return 0;
        double read = usage.CacheReadInputTokens ?? 0, write = usage.CacheCreationInputTokens ?? 0;
        return (usage.InputTokens * p.input + write * p.input * 1.25 + read * p.cacheRead + usage.OutputTokens * p.output)
               / 1_000_000;
    }

    /// <summary>Logs the API's error message on a 4xx/5xx (the SDK exception only carries the status code).</summary>
    private sealed class ErrorBodyLogger : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.Content != null)
            {
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger.Log($"[Agent] API {(int)response.StatusCode}: {body[..Math.Min(body.Length, 800)]}");
            }
            return response;
        }
    }
}

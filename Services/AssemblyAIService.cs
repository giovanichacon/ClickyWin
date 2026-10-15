using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// One push-to-talk transcription session over AssemblyAI Streaming v3.
///
///   URL:    wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&amp;encoding=pcm_s16le&amp;speech_model=…
///   Auth:   Authorization: {api_key}
///   Audio:  binary PCM16 frames, 50–1000 ms each (we send 100 ms)
///   Client: {"type":"ForceEndpoint"} ends the current turn now;
///           {"type":"Terminate"} flushes, sends final Turns, then "Termination", then closes 1000
///   Server: "Begin", "Turn" {turn_order, transcript, end_of_turn}, "Termination"
///
/// Audio can be enqueued before the socket is open: everything goes through one outbox
/// channel drained by a single send loop, so the first words spoken while connecting are
/// kept (pre-roll) and ClientWebSocket never sees concurrent SendAsync calls.
///
/// The model ends a turn on short pauses, so a single press can produce several turns;
/// they are all kept and joined in turn order.
/// </summary>
public sealed class AssemblyAIService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    // AssemblyAI caps concurrent sessions per account ("1008 Too many concurrent sessions"). Rapid presses
    // open sockets faster than old ones close, so a new one waits briefly for a free slot, and a refused
    // one is retried: audio keeps queuing meanwhile, nothing said is lost.
    private const int MaxOpenSockets = 3;
    private const int ConnectAttempts = 3;
    private static readonly SemaphoreSlim OpenSockets = new(MaxOpenSockets, MaxOpenSockets);
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private int _holdsSlot;
    private static readonly byte[] ForceEndpointMessage = """{"type":"ForceEndpoint"}"""u8.ToArray();
    private static readonly byte[] TerminateMessage = """{"type":"Terminate"}"""u8.ToArray();

    private readonly record struct Outgoing(byte[] Payload, WebSocketMessageType Type);

    private readonly string _apiKey;
    private readonly Uri _uri;
    private ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Outgoing> _outbox =
        Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SortedDictionary<int, string> _turns = new();
    private readonly object _turnsLock = new();

    private Task _connect = Task.CompletedTask;
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private int _audioFrames;
    private int _disposed;

    /// <summary>Each finished turn's text, as it ends (hands-free task session). Fires on the receive loop: don't block.</summary>
    public event Action<string>? TurnEnded;

    /// <summary>Set when the session failed (connect error, abnormal close). Null on success.</summary>
    public string? Error { get; private set; }

    /// <param name="keyterms">Personal dictionary: boosted names/terms (API limit 100 terms of ≤50 chars).</param>
    public AssemblyAIService(string apiKey, string endpoint, string speechModel, IReadOnlyList<string>? keyterms = null)
    {
        _apiKey = apiKey;
        var url = $"{endpoint}?sample_rate=16000&encoding=pcm_s16le" +
                  $"&speech_model={Uri.EscapeDataString(speechModel)}";
        var terms = keyterms?.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 50).Take(100).ToList();
        if (terms is { Count: > 0 })
            url += "&keyterms_prompt=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(terms));
        _uri = new Uri(url);
    }

    /// <summary>
    /// Checks a key with the REST API (lists at most one transcript, nothing is billed).
    /// </summary>
    public static async Task<(bool ok, string message)> TestKeyAsync(string key)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, "https://api.assemblyai.com/v2/transcript?limit=1");
            request.Headers.TryAddWithoutValidation("Authorization", key.Trim());
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, "OK")
                : (false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Queues a PCM16 frame. Callable from any thread, before or after the socket opens.</summary>
    public void EnqueueAudio(byte[] pcm16)
    {
        if (_outbox.Writer.TryWrite(new Outgoing(pcm16, WebSocketMessageType.Binary)))
            Interlocked.Increment(ref _audioFrames);
    }

    /// <summary>Connects and starts the send/receive loops. Never throws; failures set <see cref="Error"/>.</summary>
    public Task ConnectAsync() => _connect = ConnectCoreAsync();

    private async Task ConnectCoreAsync()
    {
        try
        {
            if (await OpenSockets.WaitAsync(SlotWait, _cts.Token).ConfigureAwait(false))
                Volatile.Write(ref _holdsSlot, 1);
            else
                Logger.Log("[ASR] Older sessions still open, connecting anyway");
        }
        catch (OperationCanceledException)
        {
            _ended.TrySetResult();
            return;
        }

        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 1) _ws = new ClientWebSocket();
            var ws = _ws;
            string? first;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                ws.Options.SetRequestHeader("Authorization", _apiKey);
                await ws.ConnectAsync(_uri, timeout.Token).ConfigureAwait(false);
                // The first message ("Begin" or "Error") says whether the session was accepted
                first = await ReceiveTextAsync(ws, buffer, message, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    Fail(timeout.IsCancellationRequested ? "connection timed out" : ex.Message);
                EndConnection();
                return;
            }
            if (_cts.IsCancellationRequested)
            {
                EndConnection();
                return;
            }

            if (first != null && IsTooManySessions(first) && attempt < ConnectAttempts)
            {
                Logger.Log($"[ASR] Too many sessions open, retrying (attempt {attempt})");
                ws.Abort();
                ws.Dispose();
                try { await Task.Delay(RetryDelay, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    EndConnection();
                    return;
                }
                continue;
            }

            if (first == null)
            {
                OnClosed(ws);
                EndConnection();
                return;
            }
            Logger.Log("[ASR] Connected");
            HandleMessage(first);
            _receiveLoop = ReceiveLoopAsync(_cts.Token);
            _sendLoop = SendLoopAsync(_cts.Token);
            return;
        }
    }

    private static bool IsTooManySessions(string json) =>
        json.Contains("concurrent", StringComparison.OrdinalIgnoreCase);

    private void EndConnection()
    {
        ReleaseSlot();
        _ended.TrySetResult();
    }

    private void ReleaseSlot()
    {
        if (Interlocked.Exchange(ref _holdsSlot, 0) == 1) OpenSockets.Release();
    }

    /// <summary>One whole text message, or null when the server closed the socket.</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buffer, MemoryStream message,
        CancellationToken ct)
    {
        message.SetLength(0);
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private void OnClosed(ClientWebSocket ws)
    {
        var status = ws.CloseStatus;
        if (status is not (WebSocketCloseStatus.NormalClosure or null))
        {
            if (Error == null) Fail($"closed {(int)status!} {ws.CloseStatusDescription}");
        }
        else
            Logger.Log("[ASR] Closed normally");
    }

    /// <summary>
    /// Call after the last audio frame was enqueued. Ends the turn, asks the server to flush,
    /// and waits for the Termination message. Returns every turn joined in order.
    /// </summary>
    public async Task<string> FinishAsync(TimeSpan timeout, CancellationToken ct)
    {
        _outbox.Writer.TryWrite(new Outgoing(ForceEndpointMessage, WebSocketMessageType.Text));
        _outbox.Writer.TryWrite(new Outgoing(TerminateMessage, WebSocketMessageType.Text));
        _outbox.Writer.TryComplete();

        try
        {
            await _ended.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Log($"[ASR] No Termination within {timeout.TotalSeconds:F0} s — using turns received so far");
        }

        var transcript = CombinedTranscript();
        Logger.Log($"[ASR] Final transcript ({_audioFrames} frames sent): \"{transcript}\"");
        return transcript;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(msg.Payload, msg.Type, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            Logger.Log($"[ASR] Send stopped: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(_ws, buffer, message, ct).ConfigureAwait(false);
                if (text == null)
                {
                    OnClosed(_ws);
                    break;
                }
                HandleMessage(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) Fail(ex.Message);
        }
        finally
        {
            EndConnection();
        }
    }

    private void HandleMessage(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (Exception ex)
        {
            Logger.Log($"[ASR] Unparseable message ({ex.Message}): {json}");
            return;
        }
        if (node == null) return;

        switch (node["type"]?.GetValue<string>())
        {
            case "Begin":
                // Unknown parameters are ignored rather than rejected, so log the model actually used
                Logger.Log($"[ASR] Session began, model={node["configuration"]?["model"]?.ToString() ?? "?"}");
                break;

            case "Turn":
                int order = node["turn_order"]?.GetValue<int>() ?? 0;
                string text = node["transcript"]?.GetValue<string>() ?? "";
                bool endOfTurn = node["end_of_turn"]?.GetValue<bool>() ?? false;
                lock (_turnsLock)
                    _turns[order] = text; // later messages for a turn supersede earlier ones
                if (endOfTurn)
                {
                    Logger.Log($"[ASR] Turn {order} ended: \"{text}\"");
                    if (text.Trim().Length > 0) TurnEnded?.Invoke(text.Trim());
                }
                break;

            case "Termination":
                Logger.Log($"[ASR] Termination (audio {node["audio_duration_seconds"]} s)");
                _ended.TrySetResult();
                break;

            case "SpeechStarted":
                break;

            case "Error":
                // e.g. {"type":"Error","error_code":1008,"error":"Unauthorized Connection: Invalid API key"},
                // sent right before the server closes the socket
                if (IsTooManySessions(json))
                {
                    Logger.Log($"[ASR] {json}");
                    Fail(L.AsrBusy);
                }
                else
                    Fail($"{node["error_code"]} {node["error"]?.GetValue<string>() ?? json}");
                break;

            default:
                Logger.Log($"[ASR] Message: {json}");
                break;
        }
    }

    private string CombinedTranscript()
    {
        lock (_turnsLock)
            return string.Join(" ", _turns.Values.Select(t => t.Trim()).Where(t => t.Length > 0));
    }

    private void Fail(string reason)
    {
        Error ??= reason;
        Logger.Error($"[ASR] {reason}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _outbox.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            // Connect first: it is what assigns the loop tasks
            await _connect.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await Task.WhenAll(_sendLoop, _receiveLoop).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { /* loops swallow their own errors; a timeout here just means we abort */ }

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch { _ws.Abort(); }
        }
        _ws.Dispose();
        ReleaseSlot();
        // _cts is deliberately not disposed: a connect racing with dispose may still read its token
    }
}

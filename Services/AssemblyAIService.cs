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

    // Session reuse: free accounts may start only 5 streaming sessions per minute (the 6th gets 1008), so
    // after a press the session stays open for a short while and the next press continues on it.
    // Streaming is billed by the time the socket is open, so the wait is short, never "always open".
    private static readonly object PoolLock = new();
    private static AssemblyAIService? _pooled; // under PoolLock: idle, waiting for the next press
    private static volatile bool _reuseOff;
    private static int _reuseMisses;
    private static readonly TimeSpan ForcedTurnWait = TimeSpan.FromSeconds(1.5);
    // No turn open at release: a final Turn normally comes 100–250 ms after ForceEndpoint when there were words.
    // shortcut: words that reach the server only after this are lost; if logs show that, raise it.
    private static readonly TimeSpan SilentGrace = TimeSpan.FromMilliseconds(400);
    private bool _turnOpen; // under _turnsLock: the server is inside a turn (SpeechStarted or a partial, no end yet)
    private readonly TimeSpan _linger; // zero = not reusable
    private int _parkVersion; // under PoolLock
    private volatile bool _parked;
    private bool _reused;
    private int _minOrder; // under _turnsLock: turns below this belong to an earlier press
    private int _lastTextOrder = -1; // under _turnsLock

    private readonly string _apiKey;
    private readonly Uri _uri;
    private ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Outgoing> _outbox =
        Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _finalTurn = new(TaskCreationOptions.RunContinuationsAsynchronously); // under _turnsLock
    private bool _finishing; // under _turnsLock
    private int _startedOrder = -1; // under _turnsLock: the turn whose first words arrived at _turnStartedAt
    private DateTime _turnStartedAt;

    private readonly SortedDictionary<int, string> _turns = new();
    private readonly object _turnsLock = new();

    private Task _connect = Task.CompletedTask;
    private Task _sendLoop = Task.CompletedTask;
    private Task _receiveLoop = Task.CompletedTask;
    private int _audioFrames;
    private int _disposed;

    /// <summary>
    /// Each finished turn's text and when its first words arrived (UTC), as it ends (hands-free task session).
    /// Fires on the receive loop: don't block.
    /// </summary>
    public event Action<string, DateTime>? TurnEnded;

    /// <summary>Set when the session failed (connect error, abnormal close). Null on success.</summary>
    public string? Error { get; private set; }

    /// <param name="keyterms">Personal dictionary: boosted names/terms (API limit 100 terms of ≤50 chars).</param>
    public AssemblyAIService(string apiKey, string endpoint, string speechModel, IReadOnlyList<string>? keyterms = null,
        IReadOnlyList<string>? languages = null, string? prompt = null, bool longTurns = false)
        : this(apiKey, BuildUri(endpoint, speechModel, keyterms, languages, prompt, longTurns, 0), TimeSpan.Zero)
    {
    }

    private AssemblyAIService(string apiKey, Uri uri, TimeSpan linger) => (_apiKey, _uri, _linger) = (apiKey, uri, linger);

    /// <summary>
    /// A session for one press: the one left open by the previous press when it fits (same key and settings,
    /// still connected), else a new one. <paramref name="lingerSeconds"/> 0 turns reuse off; hands-free never reuses.
    /// </summary>
    public static AssemblyAIService Rent(string apiKey, string endpoint, string speechModel, IReadOnlyList<string>? keyterms,
        IReadOnlyList<string>? languages, string? prompt, bool longTurns, int lingerSeconds)
    {
        if (longTurns || _reuseOff) lingerSeconds = 0;
        var uri = BuildUri(endpoint, speechModel, keyterms, languages, prompt, longTurns, lingerSeconds);
        if (lingerSeconds > 0)
        {
            AssemblyAIService? pooled;
            lock (PoolLock) (pooled, _pooled) = (_pooled, null);
            if (pooled != null)
            {
                if (pooled._apiKey == apiKey && pooled._uri == uri && pooled.IsOpen)
                {
                    pooled.StartNextPress();
                    return pooled;
                }
                _ = pooled.DisposeAsync(); // settings changed, or the server closed it while idle
            }
        }
        return new AssemblyAIService(apiKey, uri, TimeSpan.FromSeconds(Math.Max(0, lingerSeconds)));
    }

    private bool IsOpen => _ws.State == WebSocketState.Open && !_ended.Task.IsCompleted && Error == null &&
                           Volatile.Read(ref _disposed) == 0;

    private void StartNextPress()
    {
        lock (_turnsLock)
        {
            _turns.Clear();
            _finishing = false;
            _minOrder = _lastTextOrder + 1;
            _startedOrder = -1;
            _finalTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Interlocked.Exchange(ref _audioFrames, 0);
        CanPark = false;
        _reused = true;
        _parked = false;
        Logger.Log("[ASR] Reusing the open session");
    }

    /// <summary>True after a clean <see cref="FinishAsync"/> on a reusable session: call <see cref="Park"/> instead of disposing.</summary>
    public bool CanPark { get; private set; }

    /// <summary>
    /// Leaves the session open for the next press. From here the pool owns it: the caller must drop its
    /// reference and not dispose it. Closes by itself when no press comes in time.
    /// </summary>
    public void Park()
    {
        AssemblyAIService? old;
        int version;
        lock (PoolLock)
        {
            (old, _pooled) = (_pooled, this);
            version = ++_parkVersion;
            _parked = true;
        }
        if (old != null) _ = old.DisposeAsync(); // two overlapping presses: keep the newer session
        _ = Task.Delay(_linger).ContinueWith(delay =>
        {
            lock (PoolLock)
            {
                if (_pooled != this || _parkVersion != version) return;
                _pooled = null;
            }
            Logger.Log($"[ASR] No press for {_linger.TotalSeconds:F0} s, closing the session");
            _ = DisposeAsync();
        }, TaskScheduler.Default);
    }

    private static Uri BuildUri(string endpoint, string speechModel, IReadOnlyList<string>? keyterms,
        IReadOnlyList<string>? languages, string? prompt, bool longTurns, int lingerSeconds)
    {
        var url = $"{endpoint}?sample_rate=16000&encoding=pcm_s16le" +
                  $"&speech_model={Uri.EscapeDataString(speechModel)}";
        // Hands-free: a thinking pause ("I've been working on… the bake script") must not end the command
        if (longTurns) url += "&min_turn_silence=800&max_turn_silence=2500";
        // Reusable session: if Clicky dies while it is open, the server ends it instead of billing up to 3 hours
        if (lingerSeconds > 0) url += $"&inactivity_timeout={lingerSeconds + 15}";
        if (languages is { Count: > 0 })
            url += "&language_codes=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(languages));
        if (!string.IsNullOrWhiteSpace(prompt))
            url += "&prompt=" + Uri.EscapeDataString(prompt);
        var terms = keyterms?.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 50).Take(100).ToList();
        if (terms is { Count: > 0 })
            url += "&keyterms_prompt=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(terms));
        return new Uri(url);
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
    public Task ConnectAsync() => _reused ? _connect : _connect = ConnectCoreAsync();

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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan Left() // one deadline for all the waits below
        {
            var left = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
        Task<Task> done;
        lock (_turnsLock)
        {
            _finishing = true;
            done = Task.WhenAny(_ended.Task, _finalTurn.Task);
        }
        _outbox.Writer.TryWrite(new Outgoing(ForceEndpointMessage, WebSocketMessageType.Text));

        // A reusable session stays open: no Terminate, the forced final Turn alone ends the press.
        bool keepOpen = _linger > TimeSpan.Zero && !_reuseOff;
        bool silent = false; // no turn open at release: nothing more will come
        TimeSpan Cap(TimeSpan wait) => Left() < wait ? Left() : wait;
        void Miss()
        {
            // The server did not answer ForceEndpoint alone: end this session the old way.
            // Three times in one run: one session per press until restart.
            keepOpen = false;
            bool off = Interlocked.Increment(ref _reuseMisses) >= 3;
            if (off) _reuseOff = true;
            Logger.Log("[ASR] No final Turn after ForceEndpoint, sending Terminate" +
                       (off ? " — session reuse is off until Clicky restarts" : ""));
        }
        if (keepOpen)
        {
            try
            {
                await _connect.WaitAsync(Left(), ct).ConfigureAwait(false); // the wait below starts once connected
                // Measured 2026-11-24 on universal-3-6-pro: when the turn already ended by itself (pause, then
                // release), ForceEndpoint gets no reply at all, although the docs promise an empty final Turn.
                bool openAtRelease;
                lock (_turnsLock) openAtRelease = _turnOpen;
                var first = openAtRelease ? ForcedTurnWait : SilentGrace;
                try
                {
                    await done.WaitAsync(Cap(first), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    bool openNow;
                    lock (_turnsLock) openNow = _turnOpen;
                    if (openAtRelease) Miss();
                    else if (!openNow)
                    {
                        silent = true;
                        Logger.Log("[ASR] No turn open at release, not waiting for one");
                    }
                    else // words came in right at release: wait for them like an open turn
                    {
                        try { await done.WaitAsync(Cap(ForcedTurnWait - SilentGrace), ct).ConfigureAwait(false); }
                        catch (TimeoutException) { Miss(); }
                    }
                }
            }
            catch (TimeoutException)
            {
                keepOpen = false; // still connecting
            }
        }
        if (!keepOpen)
        {
            _outbox.Writer.TryWrite(new Outgoing(TerminateMessage, WebSocketMessageType.Text));
            _outbox.Writer.TryComplete();
        }

        if (!silent)
        {
            try
            {
                // The forced end of the turn carries the last words; Termination can come a second later.
                await done.WaitAsync(Left(), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Log($"[ASR] No Termination within {timeout.TotalSeconds:F0} s — using turns received so far");
            }
        }
        CanPark = keepOpen && IsOpen;

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
                DateTime started;
                lock (_turnsLock)
                {
                    if (order < _minOrder)
                    {
                        // Reused session: a late message for an earlier press. An empty forced turn is still this press's answer.
                        if (endOfTurn && _finishing && text.Length == 0) _finalTurn.TrySetResult();
                        break;
                    }
                    if (text.Length > 0 && order > _lastTextOrder) _lastTextOrder = order;
                    if (endOfTurn) _turnOpen = false;
                    else if (text.Length > 0) _turnOpen = true;
                    _turns[order] = text; // later messages for a turn supersede earlier ones
                    if (endOfTurn && _finishing) _finalTurn.TrySetResult();
                    if (text.Length > 0 && order != _startedOrder) (_startedOrder, _turnStartedAt) = (order, DateTime.UtcNow);
                    started = order == _startedOrder ? _turnStartedAt : DateTime.UtcNow;
                }
                if (endOfTurn)
                {
                    Logger.Log($"[ASR] Turn {order} ended: \"{text}\"");
                    if (text.Trim().Length > 0) TurnEnded?.Invoke(text.Trim(), started);
                }
                break;

            case "Termination":
                Logger.Log($"[ASR] {json}"); // whole message: shows the billed session seconds
                _ended.TrySetResult();
                break;

            case "SpeechStarted": // once per turn, right before its first Turn message
                lock (_turnsLock) _turnOpen = true;
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
        if (_parked) Logger.Log($"[ASR] Idle session ended: {reason}"); // nobody is talking: not an error to show
        else Logger.Error($"[ASR] {reason}");
    }

    /// <summary>Returns at once; the socket closes in the background (see <see cref="CloseAsync"/>).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        _ = Task.Run(CloseAsync);
        return ValueTask.CompletedTask;
    }

    private async Task CloseAsync()
    {
        // Cancelling before Terminate is sent leaves the session open on AssemblyAI's side, and those count
        // against the concurrent-session cap (1008) until the server times them out. Ask for a clean end first.
        _outbox.Writer.TryWrite(new Outgoing(TerminateMessage, WebSocketMessageType.Text)); // no-op after FinishAsync
        _outbox.Writer.TryComplete();
        try
        {
            await _connect.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (_ws.State == WebSocketState.Open)
                await _ended.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { /* timeout or failed connect: close anyway */ }

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

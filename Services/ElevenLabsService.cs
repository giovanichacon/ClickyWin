using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// ElevenLabs text-to-speech, streamed: each sentence is requested as raw 24 kHz PCM from the
/// /stream endpoint and played while it downloads, and the next sentence is fetched while the
/// previous one is still playing. Speech starts after the first sentence instead of the whole reply.
/// Free plans can only use the account's premade/own voices through the API (library voices
/// return 402 paid_plan_required).
/// </summary>
public sealed class ElevenLabsService(AppSettings settings) : IDisposable
{
    private static readonly HttpClient Http = new();
    private static readonly WaveFormat Pcm = new(24000, 16, 1);

    private readonly AppSettings _settings = settings;
    private Playback? _current;

    /// <summary>Starts an audio stream; append sentences in order, then <see cref="Playback.DrainAsync"/>.</summary>
    /// <param name="onCaption">Text heard so far (word by word, synced to playback); null = no captions.</param>
    public Playback BeginPlayback(Action? onPlaybackStarting, Action<string>? onCaption = null) =>
        BeginPlayback(_settings.ElevenLabsApiKey, _settings.EffectiveElevenLabsVoiceId, _settings.EffectiveVoiceSpeed,
            _settings.SpeakerDeviceId, onPlaybackStarting, onCaption);

    private Playback BeginPlayback(string apiKey, string voiceId, double speed, string deviceId, Action? onPlaybackStarting,
        Action<string>? onCaption = null)
    {
        var playback = new Playback(this, apiKey, voiceId, speed, deviceId, onPlaybackStarting, onCaption);
        Interlocked.Exchange(ref _current, playback)?.Stop();
        return playback;
    }

    /// <summary>Speaks one text with an explicit key, voice, speed and output device (Setup preview).</summary>
    public async Task SpeakWithAsync(string text, string apiKey, string voiceId, double speed, string deviceId,
        Action? onPlaybackStarting, CancellationToken ct = default)
    {
        using var playback = BeginPlayback(apiKey, voiceId, speed, deviceId, onPlaybackStarting);
        await playback.AppendAsync(text, "", ct).ConfigureAwait(false);
        await playback.DrainAsync(ct).ConfigureAwait(false);
    }

    public void StopPlayback() => _current?.Stop();

    public void Dispose() => Interlocked.Exchange(ref _current, null)?.Dispose();

    /// <summary>One continuous audio stream fed sentence by sentence.</summary>
    public sealed class Playback : IDisposable
    {
        private readonly ElevenLabsService _owner;
        private readonly string _apiKey, _voiceId;
        private readonly double _speed;
        private readonly Action? _onPlaybackStarting;
        private readonly BufferedWaveProvider _buffer = new(Pcm)
        {
            BufferDuration = TimeSpan.FromMinutes(5),
            DiscardOnBufferOverflow = false,
        };
        private readonly WasapiOut _player;
        private bool _playing, _stopped;

        // Captions: each sentence's byte range in the stream; heard position = appended - still buffered - device latency
        private sealed class Segment(long start, string text) { public readonly long Start = start; public readonly string Text = text; public long End = -1; }
        private const int BytesPerSecond = 48000; // pcm_24000, 16-bit mono
        private const int LatencyBytes = BytesPerSecond / 10; // WasapiOut 100 ms
        private const int BytesPerCharGuess = BytesPerSecond * 65 / 1000; // ~15 chars/s, only until a sentence finishes downloading
        private readonly Action<string>? _onCaption;
        private readonly List<Segment> _segments = [];
        private long _appended;
        private System.Threading.Timer? _captionTimer;
        private string _lastCaption = "";

        internal Playback(ElevenLabsService owner, string apiKey, string voiceId, double speed, string deviceId, Action? onPlaybackStarting,
            Action<string>? onCaption)
        {
            _onCaption = onCaption;
            _player = AudioOutput.Open(deviceId);
            _owner = owner;
            _apiKey = apiKey.Trim();
            _voiceId = voiceId;
            _speed = Math.Clamp(speed, 0.7, 1.2);
            _onPlaybackStarting = onPlaybackStarting;
            _player.Init(_buffer);
        }

        /// <summary>Has any audio started playing? (Failures before that fall back to the Windows voice.)</summary>
        public bool Started => _playing;

        /// <summary>Downloads one sentence into the stream; playback starts once ~150 ms is buffered.</summary>
        public async Task AppendAsync(string text, string previousText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text) || _stopped) return;

            var cfg = _owner._settings.Config;
            var url = $"{cfg.Endpoints.ElevenLabs.TrimEnd('/')}/v1/text-to-speech/{Uri.EscapeDataString(_voiceId)}" +
                      "/stream?output_format=pcm_24000";
            var body = new
            {
                text,
                model_id = cfg.Speech.ElevenLabsModel,
                // Earlier sentences keep the intonation continuous across requests
                previous_text = string.IsNullOrEmpty(previousText) ? null : previousText,
                voice_settings = new { stability = 0.5, similarity_boost = 0.75, speed = _speed },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("xi-api-key", _apiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"ElevenLabs {(int)response.StatusCode}: {ErrorMessage(err)}");
            }

            var segment = new Segment(Interlocked.Read(ref _appended), text);
            lock (_segments) _segments.Add(segment);
            try { await ReadAudioAsync(response, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref segment.End, Interlocked.Read(ref _appended)); }
        }

        private async Task ReadAudioAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var chunk = new byte[8192];
            int carry = 0; // a 16-bit sample can be split across two reads
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(carry), ct).ConfigureAwait(false)) > 0)
            {
                int total = carry + read;
                int even = total & ~1;
                _buffer.AddSamples(chunk, 0, even);
                Interlocked.Add(ref _appended, even);
                carry = total - even;
                if (carry == 1) chunk[0] = chunk[even];

                if (!_playing && _buffer.BufferedDuration >= TimeSpan.FromMilliseconds(150)) StartPlaying();
            }
        }

        /// <summary>Waits until everything appended has been heard.</summary>
        public async Task DrainAsync(CancellationToken ct)
        {
            if (_stopped) return;
            if (!_playing && _buffer.BufferedBytes > 0) StartPlaying();
            using var reg = ct.Register(Stop);
            while (!_stopped && _buffer.BufferedBytes > 0)
                await Task.Delay(30, ct).ConfigureAwait(false);
            if (_playing && !_stopped)
                await Task.Delay(150, ct).ConfigureAwait(false); // the device's own buffer
            RevealCaption(all: true);
        }

        /// <summary>Sends the words heard so far: a sentence starts at its first byte, its words by character share.</summary>
        private void RevealCaption(bool all = false)
        {
            if (_onCaption == null) return;
            lock (_segments) // Stop takes this lock too, so no caption fires after Stop returns
            {
                if (_stopped) return;
                long appended = Interlocked.Read(ref _appended);
                long heard = all ? long.MaxValue : appended - _buffer.BufferedBytes - LatencyBytes;
                var text = new StringBuilder();
                foreach (var seg in _segments)
                {
                    if (heard < seg.Start) break;
                    long segEnd = Interlocked.Read(ref seg.End);
                    long end = segEnd >= 0 ? segEnd : Math.Max(appended, seg.Start + (long)seg.Text.Length * BytesPerCharGuess);
                    text.Append(StartedWords(seg.Text, (double)(heard - seg.Start) / Math.Max(1, end - seg.Start))).Append(' ');
                }
                var caption = text.ToString().TrimEnd();
                if (caption.Length == 0 || caption == _lastCaption) return;
                _lastCaption = caption;
                _onCaption(caption);
            }
        }

        /// <summary>The words of <paramref name="text"/> that have started by <paramref name="fraction"/> of its duration.</summary>
        internal static string StartedWords(string text, double fraction)
        {
            if (fraction >= 1) return text;
            int end = (int)(Math.Max(0, fraction) * text.Length);
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            return text[..end].TrimEnd();
        }

        private void StartPlaying()
        {
            _playing = true;
            _onPlaybackStarting?.Invoke();
            _player.Play();
            if (_onCaption != null) _captionTimer = new System.Threading.Timer(_ => RevealCaption(), null, 0, 50);
        }

        public void Stop()
        {
            lock (_segments) _stopped = true;
            _captionTimer?.Dispose();
            try { _player.Stop(); }
            catch (Exception ex) { Logger.Log($"[TTS] Stop error: {ex.Message}"); }
        }

        public void Dispose()
        {
            Stop();
            _player.Dispose();
            Interlocked.CompareExchange(ref _owner._current, null, this);
        }
    }

    /// <summary>Pulls "detail.message" out of an ElevenLabs error body when there is one.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            var detail = JsonNode.Parse(body)?["detail"];
            var message = detail?["message"]?.GetValue<string>() ?? detail?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch { /* not JSON */ }
        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>Checks a key against the account endpoint (no characters used).</summary>
    public static async Task<(bool ok, string message)> TestKeyAsync(string key, string baseUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/user");
            request.Headers.Add("xi-api-key", key.Trim());
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, $"{(int)response.StatusCode} {ErrorMessage(await response.Content.ReadAsStringAsync())}");
            var tier = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["subscription"]?["tier"]?.ToString();
            return (true, tier ?? "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Voices this key may use through the API: premade voices and the account's own.
    /// Library voices are left out because free plans can't use them via the API.
    /// </summary>
    public static async Task<List<(string id, string name)>> ListVoicesAsync(string key, string baseUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v2/voices?page_size=100");
        request.Headers.Add("xi-api-key", key.Trim());
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var voices = new List<(string, string)>();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        foreach (var v in json?["voices"]?.AsArray() ?? [])
        {
            var category = v?["category"]?.ToString();
            if (category is "professional" or "famous") continue; // library voices
            var id = v?["voice_id"]?.ToString();
            var name = v?["name"]?.ToString();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                voices.Add((id, name));
        }
        return voices;
    }
}

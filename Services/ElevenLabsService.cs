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
    public Playback BeginPlayback(Action? onPlaybackStarting) =>
        BeginPlayback(_settings.ElevenLabsApiKey, _settings.EffectiveElevenLabsVoiceId, _settings.EffectiveVoiceSpeed, onPlaybackStarting);

    private Playback BeginPlayback(string apiKey, string voiceId, double speed, Action? onPlaybackStarting)
    {
        var playback = new Playback(this, apiKey, voiceId, speed, onPlaybackStarting);
        Interlocked.Exchange(ref _current, playback)?.Stop();
        return playback;
    }

    /// <summary>Speaks one text with an explicit key, voice and speed (Setup preview).</summary>
    public async Task SpeakWithAsync(string text, string apiKey, string voiceId, double speed,
        Action? onPlaybackStarting, CancellationToken ct = default)
    {
        using var playback = BeginPlayback(apiKey, voiceId, speed, onPlaybackStarting);
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
        private readonly WasapiOut _player = new(AudioClientShareMode.Shared, 100);
        private bool _playing, _stopped;

        internal Playback(ElevenLabsService owner, string apiKey, string voiceId, double speed, Action? onPlaybackStarting)
        {
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

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var chunk = new byte[8192];
            int carry = 0; // a 16-bit sample can be split across two reads
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(carry), ct).ConfigureAwait(false)) > 0)
            {
                int total = carry + read;
                int even = total & ~1;
                _buffer.AddSamples(chunk, 0, even);
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
        }

        private void StartPlaying()
        {
            _playing = true;
            _onPlaybackStarting?.Invoke();
            _player.Play();
        }

        public void Stop()
        {
            _stopped = true;
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

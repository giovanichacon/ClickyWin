using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClickyWindows.Settings;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Synthesizes speech via ElevenLabs TTS API and plays it back using NAudio.
/// Mirrors the ElevenLabsTTSClient in macOS Clicky.
/// Free plans can only use the account's premade/own voices through the API (library
/// voices return 402 paid_plan_required).
/// </summary>
public class ElevenLabsService : IDisposable
{
    private static readonly HttpClient Http = new();
    private readonly AppSettings _settings;
    private IWavePlayer? _player;
    private Mp3FileReader? _reader;

    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;

    public ElevenLabsService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Synthesizes text to speech and plays it. Returns when playback completes.
    /// <paramref name="onPlaybackStarting"/> runs right before audio starts — after the MP3
    /// has been fetched and decoded — so the caller's spinner covers the whole HTTP fetch.
    /// </summary>
    public Task SpeakAsync(string text, Action? onPlaybackStarting, CancellationToken ct = default) =>
        SpeakWithAsync(text, _settings.ElevenLabsApiKey, _settings.EffectiveElevenLabsVoiceId, onPlaybackStarting, ct);

    /// <summary>Same as <see cref="SpeakAsync"/> with an explicit key and voice (Setup preview).</summary>
    public async Task SpeakWithAsync(string text, string apiKey, string voiceId, Action? onPlaybackStarting,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        byte[] mp3 = await FetchAudioAsync(text, apiKey, voiceId, ct);
        ct.ThrowIfCancellationRequested();
        await PlayMp3Async(mp3, onPlaybackStarting, ct);
    }

    public void StopPlayback()
    {
        _player?.Stop();
    }

    private async Task<byte[]> FetchAudioAsync(string text, string apiKey, string voiceId, CancellationToken ct)
    {
        var url = $"{_settings.Config.Endpoints.ElevenLabs.TrimEnd('/')}/v1/text-to-speech/" +
                  Uri.EscapeDataString(voiceId);

        var body = new
        {
            text,
            model_id = _settings.Config.Speech.ElevenLabsModel,
            voice_settings = new
            {
                stability = 0.5,
                similarity_boost = 0.75,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("xi-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"ElevenLabs {(int)response.StatusCode}: {ErrorMessage(err)}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
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

    private async Task PlayMp3Async(byte[] mp3Bytes, Action? onPlaybackStarting, CancellationToken ct)
    {
        // Dispose previous player
        _player?.Dispose();
        _reader?.Dispose();

        var ms = new MemoryStream(mp3Bytes);
        _reader = new Mp3FileReader(ms);
        var player = new WasapiOut();
        _player = player;
        player.Init(_reader);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) tcs.TrySetException(e.Exception);
            else tcs.TrySetResult();
        };

        using var reg = ct.Register(() =>
        {
            player.Stop();
            tcs.TrySetCanceled(ct);
        });

        onPlaybackStarting?.Invoke();
        player.Play();
        await tcs.Task;
    }

    public void Dispose()
    {
        _player?.Dispose();
        _reader?.Dispose();
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

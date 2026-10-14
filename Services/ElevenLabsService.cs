using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClickyWindows.Settings;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Synthesizes speech via ElevenLabs TTS API and plays it back using NAudio.
/// Mirrors the ElevenLabsTTSClient in macOS Clicky.
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
    public async Task SpeakAsync(string text, Action? onPlaybackStarting, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        byte[] mp3 = await FetchAudioAsync(text, ct);
        ct.ThrowIfCancellationRequested();
        await PlayMp3Async(mp3, onPlaybackStarting, ct);
    }

    public void StopPlayback()
    {
        _player?.Stop();
    }

    private async Task<byte[]> FetchAudioAsync(string text, CancellationToken ct)
    {
        var url = $"{_settings.Config.Endpoints.ElevenLabs.TrimEnd('/')}/v1/text-to-speech/" +
                  Uri.EscapeDataString(_settings.EffectiveElevenLabsVoiceId);

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

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("xi-api-key", _settings.ElevenLabsApiKey);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"ElevenLabs error {(int)response.StatusCode}: {err}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
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
            return response.IsSuccessStatusCode
                ? (true, "OK")
                : (false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

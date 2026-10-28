using ClickyWindows.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClickyWindows.Services;

/// <summary>Playback on the speaker chosen in Settings > Voice ("" = Windows default).</summary>
public static class AudioOutput
{
    public static WasapiOut Open(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return new WasapiOut(AudioCaptureService.ResolveDevice(enumerator, deviceId, DataFlow.Render), AudioClientShareMode.Shared, true, 100);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Audio] Speaker failed ({ex.Message}), using the default");
            return new WasapiOut(AudioClientShareMode.Shared, 100);
        }
    }

    /// <summary>Two short soft notes (Setup "Test sound").</summary>
    public static async Task PlayTestSoundAsync(string deviceId)
    {
        ISampleProvider Note(double hz) => new SignalGenerator(44100, 1) { Frequency = hz, Gain = 0.15, Type = SignalGeneratorType.Sin }
            .Take(TimeSpan.FromMilliseconds(180));
        var chime = new ConcatenatingSampleProvider([Note(660), Note(880)]);
        using var player = Open(deviceId);
        var done = new TaskCompletionSource();
        player.PlaybackStopped += (_, _) => done.TrySetResult();
        player.Init(chime);
        player.Play();
        await done.Task;
    }
}

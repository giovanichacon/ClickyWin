using ClickyWindows.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Live input level of one microphone, for the Setup window's meter. Records only while
/// open (the Windows mic indicator shows during that time). Independent of the main
/// capture client so testing a device never disturbs push-to-talk.
/// </summary>
public sealed class MicLevelMeter : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private SampleDecoder _decoder;
    private float _peak;
    private bool _disposed;

    public bool Start(string deviceId)
    {
        lock (_gate) return !_disposed && StartCore(deviceId);
    }

    private bool StartCore(string deviceId)
    {
        Stop();
        try
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null); // events on the capture thread
            try { _capture = new WasapiCapture(AudioCaptureService.ResolveDevice(_enumerator, deviceId)); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

            if (!SampleDecoder.TryCreate(_capture.WaveFormat, out _decoder))
            {
                Stop();
                return false;
            }
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[Setup] Mic preview failed: {ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>Highest level (0..1) since the last call.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var decoder = _decoder;
        float peak = 0;
        for (int p = 0; p + decoder.BlockAlign <= e.BytesRecorded; p += decoder.BlockAlign)
            peak = Math.Max(peak, Math.Abs(decoder.Read(e.Buffer, p))); // first channel is enough
        if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);
    }

    private void Stop()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnData;
        try { _capture.Dispose(); } catch { /* device gone */ }
        _capture = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Stop();
        }
        _enumerator.Dispose();
    }
}

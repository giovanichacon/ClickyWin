using ClickyWindows.Helpers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClickyWindows.Services;

/// <summary>
/// Captures the default microphone via WASAPI (event-driven, shared mode) and emits
/// fixed 100 ms frames of PCM16 16 kHz mono — the format AssemblyAI streaming expects
/// (it rejects chunks shorter than 50 ms).
///
/// One long-lived instance: opening a WASAPI capture client costs 0.2–1 s, restarting an
/// already-initialized one ~80 ms to first audio. So the client is created once, reused for
/// every press, and only recreated when the default mic changes or the device fails. The mic
/// is only *recording* (privacy indicator on) between StartAsync and Recording.StopAsync.
///
/// Frame/level callbacks run on NAudio's capture thread and must stay non-blocking:
/// never marshal to the UI thread synchronously from them.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int TargetSampleRate = 16000;
    private const int FrameBytes = TargetSampleRate * 2 / 10; // 100 ms of PCM16 mono

    // KSDATAFORMAT_SUBTYPE_* GUIDs, for WAVE_FORMAT_EXTENSIBLE mix formats
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm       = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher;
    private WasapiCapture? _capture;
    private volatile bool _stale;
    private volatile Recording? _active;
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Decoder for the current capture format
    private bool _isFloat;
    private int _bytesPerSample;
    private int _channels;
    private readonly float[] _monoBuffer = new float[8192];
    private readonly float[] _readBuffer = new float[4096];

    public AudioCaptureService()
    {
        _watcher = new DeviceWatcher(() => _stale = true);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
    }

    /// <summary>
    /// Starts recording into a new <see cref="Recording"/>. Any recording still running is
    /// stopped (and its tail flushed to its own sink) first. Returns null if no usable mic.
    /// </summary>
    public async Task<Recording?> StartAsync(Action<byte[]>? onFrame, Action<float>? onLevel)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active != null) await StopCoreAsync(_active).ConfigureAwait(false);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var capture = EnsureCapture();
                    var recording = new Recording(this, onFrame, onLevel, capture.WaveFormat.SampleRate);
                    _active = recording;
                    _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    capture.StartRecording();
                    return recording;
                }
                catch (Exception ex)
                {
                    _active = null;
                    Logger.Log($"[Audio] Start failed (attempt {attempt + 1}): {ex.Message}");
                    DisposeCapture(); // device vanished or changed: rebuild once
                }
            }
            Logger.Error("[Audio] Could not open the microphone");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Opens and briefly runs the mic once so the first real press starts fast.
    /// The Windows mic indicator blinks for a moment when this runs.
    /// </summary>
    public async Task WarmUpAsync()
    {
        var gotAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = await StartAsync(null, _ => gotAudio.TrySetResult()).ConfigureAwait(false);
        if (recording == null) return;
        await Task.WhenAny(gotAudio.Task, Task.Delay(2000)).ConfigureAwait(false);
        await recording.StopAsync().ConfigureAwait(false);
        Logger.Log("[Audio] Warm-up done");
    }

    private WasapiCapture EnsureCapture()
    {
        if (_capture != null && !_stale) return _capture;

        DisposeCapture();
        _stale = false;

        // WasapiCapture posts RecordingStopped to the SynchronizationContext it was created on.
        // Create it without one so that event arrives on the capture thread even if we were
        // called from the UI thread (StopAsync waits for it).
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        WasapiCapture capture;
        try { capture = new WasapiCapture(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        var fmt = capture.WaveFormat;
        if (!TryDescribe(fmt, out _isFloat, out _bytesPerSample))
        {
            capture.Dispose();
            throw new NotSupportedException($"unsupported microphone format {fmt}");
        }
        _channels = fmt.Channels;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        _capture = capture;
        Logger.Log($"[Audio] Mic opened: {fmt.SampleRate} Hz, {fmt.Channels} ch, " +
                   $"{fmt.BitsPerSample} bit {(_isFloat ? "float" : "PCM")}");
        return capture;
    }

    private async Task StopRecordingAsync(Recording recording)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active == recording) await StopCoreAsync(recording).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopCoreAsync(Recording recording)
    {
        _capture?.StopRecording();
        try { await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Logger.Log("[Audio] Capture thread did not stop within 1 s — rebuilding the mic");
            _stale = true;
        }
        _active = null;
        recording.Flush();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Error($"[Audio] Microphone stopped unexpectedly: {e.Exception.Message}");
            _stale = true;
        }
        _stopped.TrySetResult();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var rec = _active;
        if (e.BytesRecorded == 0 || rec == null) return;

        int blockAlign = _bytesPerSample * _channels;
        int frames = e.BytesRecorded / blockAlign;
        int total = frames;
        double sumSquares = 0;

        // Downmix to mono float in slices that fit the scratch buffer
        int offset = 0;
        while (frames > 0)
        {
            int n = Math.Min(frames, _monoBuffer.Length);
            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                int p = offset + i * blockAlign;
                for (int ch = 0; ch < _channels; ch++, p += _bytesPerSample)
                    sum += ReadSample(e.Buffer, p);
                float mono = sum / _channels;
                _monoBuffer[i] = mono;
                sumSquares += mono * mono;
            }
            rec.Queue.Write(_monoBuffer.AsSpan(0, n));
            offset += n * blockAlign;
            frames -= n;
        }

        if (total > 0)
            rec.OnLevel?.Invoke(Math.Clamp((float)Math.Sqrt(sumSquares / total) * 10f, 0f, 1f));

        rec.Pump(_readBuffer);
    }

    private float ReadSample(byte[] buf, int p)
    {
        if (_isFloat)
            return BitConverter.ToSingle(buf, p);

        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buf, p) / 32768f,
            3 => ((buf[p] << 8 | buf[p + 1] << 16 | buf[p + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(buf, p) / 2147483648f,
            _ => 0f,
        };
    }

    private static bool TryDescribe(WaveFormat fmt, out bool isFloat, out int bytesPerSample)
    {
        bytesPerSample = fmt.BitsPerSample / 8;
        var encoding = fmt.Encoding;
        if (fmt is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == SubtypeIeeeFloat) encoding = WaveFormatEncoding.IeeeFloat;
            else if (ext.SubFormat == SubtypePcm) encoding = WaveFormatEncoding.Pcm;
        }

        isFloat = encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4;
        bool isPcm = encoding == WaveFormatEncoding.Pcm && bytesPerSample is 2 or 3 or 4;
        return (isFloat || isPcm) && fmt.Channels > 0;
    }

    private void DisposeCapture()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.Dispose(); } catch { /* device already gone */ }
        _capture = null;
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        DisposeCapture();
        _enumerator.Dispose();
    }

    /// <summary>One press worth of audio: owns the resampler state and the partial frame.</summary>
    public sealed class Recording
    {
        private readonly AudioCaptureService _owner;
        private readonly Action<byte[]>? _onFrame;
        private readonly ISampleProvider _output; // Queue, or a resampler reading from it
        private byte[] _frame = new byte[FrameBytes];
        private int _framePos;
        private int _framesEmitted;

        internal MonoSampleQueue Queue { get; }
        internal Action<float>? OnLevel { get; }

        internal Recording(AudioCaptureService owner, Action<byte[]>? onFrame, Action<float>? onLevel, int sourceRate)
        {
            _owner = owner;
            _onFrame = onFrame;
            OnLevel = onLevel;
            Queue = new MonoSampleQueue(sourceRate);
            // WDL keeps its filter state between reads, so there are no clicks at buffer boundaries
            _output = sourceRate == TargetSampleRate ? Queue : new WdlResamplingSampleProvider(Queue, TargetSampleRate);
        }

        /// <summary>
        /// Stops the mic, waits for the capture thread's last buffer, then emits the remaining
        /// partial frame padded with silence. Safe to call more than once.
        /// </summary>
        public Task StopAsync() => _owner.StopRecordingAsync(this);

        // Capture thread: pull whatever the resampler can produce into 100 ms PCM16 frames
        internal void Pump(float[] scratch)
        {
            int read;
            while ((read = _output.Read(scratch, 0, scratch.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    short s = (short)(Math.Clamp(scratch[i], -1f, 1f) * 32767f);
                    _frame[_framePos++] = (byte)s;
                    _frame[_framePos++] = (byte)(s >> 8);
                    if (_framePos == FrameBytes) EmitFrame();
                }
            }
        }

        internal void Flush()
        {
            if (_framePos > 0)
            {
                Array.Clear(_frame, _framePos, FrameBytes - _framePos);
                EmitFrame();
            }
            if (_onFrame != null)
                Logger.Log($"[Audio] Recording stopped — {_framesEmitted} frames ({_framesEmitted / 10.0:F1} s)");
        }

        private void EmitFrame()
        {
            var frame = _frame;
            _frame = new byte[FrameBytes];
            _framePos = 0;
            _framesEmitted++;
            _onFrame?.Invoke(frame);
        }
    }

    /// <summary>
    /// Mono float FIFO exposed as an ISampleProvider so NAudio's WDL resampler can stream
    /// from it. Only touched from the capture thread (and Flush, after that thread stopped).
    /// </summary>
    internal sealed class MonoSampleQueue(int sampleRate) : ISampleProvider
    {
        private float[] _buffer = new float[sampleRate]; // 1 s, grows if needed
        private int _start, _count;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public void Write(ReadOnlySpan<float> samples)
        {
            if (_count + samples.Length > _buffer.Length)
            {
                var bigger = new float[Math.Max(_buffer.Length * 2, _count + samples.Length)];
                int moved = Read(bigger, 0, _count);
                _buffer = bigger;
                _start = 0;
                _count = moved;
            }

            int end = (_start + _count) % _buffer.Length;
            int first = Math.Min(samples.Length, _buffer.Length - end);
            samples[..first].CopyTo(_buffer.AsSpan(end));
            samples[first..].CopyTo(_buffer);
            _count += samples.Length;
        }

        public int Read(float[] dest, int offset, int count)
        {
            int n = Math.Min(count, _count);
            int first = Math.Min(n, _buffer.Length - _start);
            Array.Copy(_buffer, _start, dest, offset, first);
            Array.Copy(_buffer, 0, dest, offset + first, n - first);
            _start = (_start + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    /// <summary>Flags the capture client stale when the default recording device changes.</summary>
    private sealed class DeviceWatcher(Action onChange) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console) onChange();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) => onChange();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

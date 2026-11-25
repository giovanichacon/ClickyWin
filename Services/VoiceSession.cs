using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// One hold-to-talk capture: owns the mic recording, the AssemblyAI socket and the cancellation.
/// Shared by Ask (<see cref="CompanionManager"/>) and Dictate/Write (<see cref="DictationManager"/>).
/// The transcription session exists before the mic starts, so every frame has a destination;
/// frames queue up (pre-roll) until the WebSocket is open.
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    private static readonly TimeSpan TranscriptTimeout = TimeSpan.FromSeconds(6);

    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public long PressedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();

    public CancellationToken Token => _cts.Token;
    public AudioCaptureService.Recording? Mic { get; private set; }
    private AssemblyAIService? _asr;
    /// <summary>Null once the session was left open for the next press, or after dispose.</summary>
    public AssemblyAIService? Asr => _asr;
    /// <summary>Everything sent to speech-to-text (PCM16 16 kHz mono), only when KeepRecordings is on.</summary>
    public MemoryStream Audio { get; } = new();
    /// <summary>Completes with false when no microphone could be opened.</summary>
    public Task<bool> MicStarted { get; private set; } = Task.FromResult(false);
    public Task AsrConnected { get; private set; } = Task.CompletedTask;

    /// <param name="onTurn">Hands-free session: each finished turn as it ends.</param>
    public void Start(AppSettings settings, AudioCaptureService mic, Action<float>? onLevel, Action<string, DateTime>? onTurn = null)
    {
        var session = _asr = AssemblyAIService.Rent(settings.AssemblyAiApiKey, settings.Config.Endpoints.AssemblyAiStreaming,
            settings.Config.Speech.AssemblyAiModel, settings.Dictionary,
            settings.Config.Speech.AssemblyAiLanguages, settings.Config.Speech.AssemblyAiPrompt, longTurns: onTurn != null,
            settings.Config.Speech.AssemblyAiLingerSeconds);
        if (onTurn != null) session.TurnEnded += onTurn;
        AsrConnected = session.ConnectAsync();
        MicStarted = Task.Run(async () =>
        {
            Action<byte[]> send = session.EnqueueAudio;
            if (settings.KeepRecordings && onTurn == null)
            {
                var asr = send;
                send = frame => { lock (Audio) Audio.Write(frame); asr(frame); };
            }
            var recording = await mic.StartAsync(send, onLevel).ConfigureAwait(false);
            if (recording == null) return false;
            Mic = recording;
            Logger.Log($"[Audio] Recording {System.Diagnostics.Stopwatch.GetElapsedTime(PressedAt).TotalMilliseconds:F0} ms after press");
            return true;
        });
    }

    /// <summary>
    /// Stops the mic and returns the final transcript ("" when nothing was heard).
    /// <paramref name="connectFailed"/> tells "couldn't reach AssemblyAI" apart from silence.
    /// </summary>
    public async Task<(string transcript, bool connectFailed)> StopAndTranscribeAsync()
    {
        if (!await MicStarted.ConfigureAwait(false)) return ("", false);
        long released = System.Diagnostics.Stopwatch.GetTimestamp();
        await Mic!.StopAsync().ConfigureAwait(false);

        var asr = _asr;
        if (asr == null) return ("", false); // disposed meanwhile
        string transcript = await asr.FinishAsync(TranscriptTimeout, Token).ConfigureAwait(false);
        Logger.Log($"[Timing] Transcript {System.Diagnostics.Stopwatch.GetElapsedTime(released).TotalMilliseconds:F0} ms after release");
        // Leave the session open for the next press. Whoever takes the reference owns it: this, or DisposeAsync.
        if (asr.CanPark && Interlocked.CompareExchange(ref _asr, null, asr) == asr) asr.Park();
        if (!string.IsNullOrWhiteSpace(transcript)) return (transcript.Trim(), false);

        await AsrConnected.ConfigureAwait(false);
        return ("", asr.Error != null);
    }

    public void Cancel() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try
        {
            if (await MicStarted.ConfigureAwait(false) && Mic != null)
                await Mic.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Session] Mic cleanup error: {ex.Message}");
        }
        try
        {
            var asr = Interlocked.Exchange(ref _asr, null);
            if (asr != null)
                await asr.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Session] ASR cleanup error: {ex.Message}");
        }
        // _cts is not disposed: late callbacks may still check Token
    }
}

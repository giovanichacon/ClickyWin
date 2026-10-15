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
    public AssemblyAIService? Asr { get; private set; }
    /// <summary>Completes with false when no microphone could be opened.</summary>
    public Task<bool> MicStarted { get; private set; } = Task.FromResult(false);
    public Task AsrConnected { get; private set; } = Task.CompletedTask;

    /// <param name="onTurn">Hands-free session: each finished turn as it ends.</param>
    public void Start(AppSettings settings, AudioCaptureService mic, Action<float>? onLevel, Action<string>? onTurn = null)
    {
        Asr = new AssemblyAIService(settings.AssemblyAiApiKey, settings.Config.Endpoints.AssemblyAiStreaming,
            settings.Config.Speech.AssemblyAiModel, settings.Dictionary);
        if (onTurn != null) Asr.TurnEnded += onTurn;
        AsrConnected = Asr.ConnectAsync();
        MicStarted = Task.Run(async () =>
        {
            var recording = await mic.StartAsync(Asr.EnqueueAudio, onLevel).ConfigureAwait(false);
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
        await Mic!.StopAsync().ConfigureAwait(false);

        string transcript = await Asr!.FinishAsync(TranscriptTimeout, Token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(transcript)) return (transcript.Trim(), false);

        await AsrConnected.ConfigureAwait(false);
        return ("", Asr.Error != null);
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
            if (Asr != null)
                await Asr.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Session] ASR cleanup error: {ex.Message}");
        }
        // _cts is not disposed: late callbacks may still check Token
    }
}

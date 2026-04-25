using KittenHerder.Platform.Windows;

namespace KittenHerder.Voice;

/// <summary>
/// Orchestrates the full voice flow:
/// HotKey down → AudioCapture.Start → tray red
/// HotKey up → AudioCapture.Stop → HallucinationGuard → Whisper → TextInjector → tray normal
/// </summary>
public sealed class VoiceController : IDisposable
{
    private readonly HotKeyListener _hotKeyListener;
    private readonly AudioCapture _audioCapture;
    private readonly HallucinationGuard _guard;
    private readonly WhisperTranscriber _transcriber;
    private readonly TextInjector _injector;
    private readonly ILogger<VoiceController> _logger;

    private string? _voiceTargetSessionId;
    private volatile bool _recording;

    /// <summary>Fires when recording state changes. UI subscribes to update tray icon.</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>Fires with status messages for UI feedback.</summary>
    public event Action<string>? StatusMessage;

    public bool IsRecording => _recording;

    public VoiceController(
        HotKeyListener hotKeyListener,
        AudioCapture audioCapture,
        HallucinationGuard guard,
        WhisperTranscriber transcriber,
        TextInjector injector,
        ILogger<VoiceController> logger)
    {
        _hotKeyListener = hotKeyListener;
        _audioCapture = audioCapture;
        _guard = guard;
        _transcriber = transcriber;
        _injector = injector;
        _logger = logger;

        _hotKeyListener.VoiceKeyDown += OnVoiceKeyDown;
        _hotKeyListener.VoiceKeyUp += OnVoiceKeyUp;
    }

    /// <summary>Set which session receives voice input. Null = focused window.</summary>
    public void SetVoiceTarget(string? sessionId)
    {
        _voiceTargetSessionId = sessionId;
    }

    public void Start()
    {
        _hotKeyListener.Start();
        _logger.LogInformation("KH: Voice controller started");
    }

    private void OnVoiceKeyDown()
    {
        if (_recording) return;

        if (!_audioCapture.Start())
        {
            StatusMessage?.Invoke("No microphone available");
            return;
        }

        _recording = true;
        RecordingStateChanged?.Invoke(true);
        _logger.LogDebug("KH: Voice recording started");
    }

    private void OnVoiceKeyUp()
    {
        if (!_recording) return;

        _recording = false;
        RecordingStateChanged?.Invoke(false);

        var samples = _audioCapture.Stop();

        // Process async — fire and forget on ThreadPool
        _ = Task.Run(() => ProcessVoiceAsync(samples));
    }

    private async Task ProcessVoiceAsync(float[] samples)
    {
        try
        {
            // Layer 1: Pre-inference audio quality check
            if (!_guard.ShouldTranscribe(samples))
            {
                _logger.LogDebug("KH: Audio rejected by hallucination guard (too short or silent)");
                return;
            }

            // Transcribe
            var result = await _transcriber.TranscribeAsync(samples);
            if (result is null)
            {
                _logger.LogDebug("KH: Whisper produced no segments");
                return;
            }

            // Layer 3: Post-inference validation
            if (!_guard.IsValidTranscription(result.Text, result.NoSpeechProb, result.Probability))
            {
                _logger.LogDebug("KH: Transcription rejected by hallucination guard: \"{Text}\"", result.Text);
                return;
            }

            // Layer 4: Destructive command check
            var dangerousMatch = _guard.CheckDestructiveCommand(result.Text);
            if (dangerousMatch is not null)
            {
                _logger.LogWarning("KH: Destructive command blocked: \"{Match}\" in \"{Text}\"",
                    dangerousMatch, result.Text);
                StatusMessage?.Invoke($"⚠️ Blocked dangerous command: {dangerousMatch}");
                return;
            }

            // Inject into target session
            var (success, message) = await _injector.InjectAsync(result.Text, _voiceTargetSessionId);

            if (success)
                _logger.LogInformation("KH: Voice → \"{Text}\" → {Message}", result.Text, message);
            else
            {
                _logger.LogWarning("KH: Voice inject failed: {Message}", message);
                StatusMessage?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KH: Voice processing error");
            StatusMessage?.Invoke($"Voice error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _hotKeyListener.VoiceKeyDown -= OnVoiceKeyDown;
        _hotKeyListener.VoiceKeyUp -= OnVoiceKeyUp;
        _hotKeyListener.Dispose();
        _audioCapture.Dispose();
        _transcriber.Dispose();
    }
}

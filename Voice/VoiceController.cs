using TinyBoss.Platform.Windows;

namespace TinyBoss.Voice;

/// <summary>
/// Orchestrates push-to-talk batch dictation:
/// HotKey down -> start mic and collect samples
/// HotKey up -> transcribe larger overlapped chunks with Whisper, merge text, then inject once.
/// This keeps synthetic input away from the target while the PTT key is held.
/// </summary>
public sealed class VoiceController : IDisposable
{
    private readonly HotKeyListener _hotKeyListener;
    private readonly AudioCapture _audioCapture;
    private readonly WhisperTranscriber _whisper;
    private readonly HallucinationGuard _guard;
    private readonly TextInjector _injector;
    private readonly ILogger<VoiceController> _logger;

    private string? _voiceTargetSessionId;
    private volatile bool _recording;

    // Batch dictation state. Samples are retained in memory during push-to-talk
    // and transcribed once on key-up in larger overlapped chunks.
    private readonly object _audioLock = new();
    private readonly List<float> _recordingSamples = new();

    private CancellationTokenSource? _sessionCts;
    private int _stopInFlight;
    private int _injectInFlight;
    private string? _lastInjectedText;
    private DateTimeOffset _lastInjectedAt;
    private VoiceInjectionTarget? _capturedTarget;

    // VAD tuning constants (16kHz sample rate)
    private const int SAMPLE_RATE = 16000;
    private const float FINAL_TAIL_THRESHOLD = 0.006f;     // Lower bar for key-up tail recovery; Whisper guard filters silence.
    private const float MIN_FINAL_TAIL_SEC = 0.5f;
    private const int MIN_FINAL_TAIL_SAMPLES = (int)(SAMPLE_RATE * MIN_FINAL_TAIL_SEC);
    private static readonly TimeSpan KeyUpSettleDelay = TimeSpan.FromMilliseconds(150);

    public event Action<bool>? RecordingStateChanged;
    public event Action<string>? StatusMessage;
    public bool IsRecording => _recording;

    public VoiceController(
        HotKeyListener hotKeyListener,
        AudioCapture audioCapture,
        WhisperTranscriber whisper,
        HallucinationGuard guard,
        TextInjector injector,
        ILogger<VoiceController> logger)
    {
        _hotKeyListener = hotKeyListener;
        _audioCapture = audioCapture;
        _whisper = whisper;
        _guard = guard;
        _injector = injector;
        _logger = logger;

        _hotKeyListener.VoiceKeyDown += OnVoiceKeyDown;
        _hotKeyListener.VoiceKeyUp += OnVoiceKeyUp;

        // Wire audio capture for batch dictation.
        _audioCapture.SamplesAvailable += OnSamplesAvailable;
    }

    public void SetVoiceTarget(string? sessionId) => _voiceTargetSessionId = sessionId;

    public VoiceStatus GetVoiceStatus() => new(
        _recording,
        _whisper.SelectedModel,
        _whisper.ActiveModel,
        _whisper.IsModelLoaded);

    public void ReloadSpeechModel()
    {
        if (_recording)
        {
            VoiceDiag("WHISPER_RELOAD_SKIPPED recording=true selected={0}", _whisper.SelectedModel);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                VoiceDiag("WHISPER_RELOAD_START selected={0}", _whisper.SelectedModel);
                await _whisper.ReloadConfiguredModelAsync();
                VoiceDiag("WHISPER_RELOAD_READY active={0}", _whisper.ActiveModel);
            }
            catch (Exception ex)
            {
                VoiceDiag("WHISPER_RELOAD_FAIL {0}: {1}", ex.GetType().Name, ex.Message);
                StatusMessage?.Invoke($"Whisper reload failed: {ex.Message}");
            }
        });
    }

    public void Start()
    {
        _hotKeyListener.Start();
        // Preload Whisper model on background thread so first use is instant.
        _whisper.PreloadAsync();
        _logger.LogInformation("KH: Voice controller started (batch Whisper GPU)");
        VoiceDiag("VOICE_STARTED voiceKey=0x{0:X} voiceMods={1}",
            _hotKeyListener.VoiceKeyConfig, _hotKeyListener.VoiceModConfig);
    }

    private void OnVoiceKeyDown()
    {
        VoiceDiag("KEY_DOWN recording={0}", _recording);
        if (_recording) return;

        _audioCapture.RetainRawAudio = false;
        if (!_audioCapture.Start())
        {
            _audioCapture.RetainRawAudio = true;
            VoiceDiag("MIC_FAIL — no microphone available");
            StatusMessage?.Invoke("No microphone available");
            return;
        }

        lock (_audioLock)
        {
            _recordingSamples.Clear();
        }
        _capturedTarget = _injector.CaptureVoiceTarget(_voiceTargetSessionId);

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();

        _recording = true;
        RecordingStateChanged?.Invoke(true);
        VoiceDiag("RECORDING_STARTED target=\"{0}\" (batch Whisper GPU)", _capturedTarget.Description);
    }

    private void OnSamplesAvailable(float[] samples)
    {
        if (!_recording) return;

        lock (_audioLock)
        {
            _recordingSamples.AddRange(samples);
        }
    }

    private void OnVoiceKeyUp()
    {
        VoiceDiag("KEY_UP recording={0}", _recording);
        if (!_recording) return;
        if (Interlocked.Exchange(ref _stopInFlight, 1) == 1)
        {
            VoiceDiag("KEY_UP_IGNORED stop already in flight");
            return;
        }

        try
        {
            var result = StopRecordingAsync().GetAwaiter().GetResult();
            var pendingText = result.Text;
            if (!string.IsNullOrWhiteSpace(pendingText))
            {
                try
                {
                    var (success, message) = InjectPendingTextOnceAsync(pendingText).GetAwaiter().GetResult();
                    VoiceDiag("INJECT_PENDING success={0} chars={1} message=\"{2}\"", success, pendingText.Length, message);
                    if (!success)
                    {
                        PreserveTranscript(pendingText, "inject-failed");
                        StatusMessage?.Invoke(message);
                    }
                }
                catch (Exception ex)
                {
                    VoiceDiag("PENDING_INJECT_ERROR {0}: {1}", ex.GetType().Name, ex.Message);
                    PreserveTranscript(pendingText, "inject-error");
                    StatusMessage?.Invoke($"Voice inject error: {ex.Message}");
                }
            }
            else if (!result.Success)
            {
                StatusMessage?.Invoke(result.Message);
            }

            VoiceDiag("SESSION_COMPLETE");
            _logger.LogInformation("KH: Voice session complete");
        }
        finally
        {
            Interlocked.Exchange(ref _stopInFlight, 0);
        }
    }

    private async Task<(bool Success, string Text, string Message)> StopRecordingAsync(CancellationToken ct = default)
    {
        VoiceDiag("STOP_RECORDING recording={0}", _recording);
        if (!_recording)
            return (false, string.Empty, "No dictation is active.");

        RecordingStateChanged?.Invoke(false);

        _audioCapture.Stop();
        _audioCapture.RetainRawAudio = true;

        float[] sessionSamples;
        lock (_audioLock)
        {
            sessionSamples = _recordingSamples.ToArray();
            _recordingSamples.Clear();
        }

        _recording = false;

        if (KeyUpSettleDelay > TimeSpan.Zero)
            await Task.Delay(KeyUpSettleDelay, ct);

        var pendingText = await TranscribeBatchSessionAsync(sessionSamples, ct);

        if (string.IsNullOrWhiteSpace(pendingText))
            return (true, string.Empty, "Nothing captured.");

        var dangerousMatch = _guard.CheckDestructiveCommand(pendingText);
        if (dangerousMatch is not null)
        {
            VoiceDiag("DESTRUCTIVE_BLOCK_PENDING \"{0}\" in {1} chars", dangerousMatch, pendingText.Length);
            PreserveTranscript(pendingText, "destructive-block");
            return (false, string.Empty, $"Blocked dangerous command: {dangerousMatch}");
        }

        return (true, pendingText, "Dictation complete.");
    }

    private async Task<string> TranscribeBatchSessionAsync(float[] samples, CancellationToken ct)
    {
        if (samples.Length < MIN_FINAL_TAIL_SAMPLES)
        {
            VoiceDiag("BATCH_SKIP short samples={0}", samples.Length);
            return string.Empty;
        }

        var rms = CalculateRms(samples);
        if (rms < FINAL_TAIL_THRESHOLD)
        {
            VoiceDiag("BATCH_SKIP quiet samples={0} seconds={1:F2} rms={2:F5}",
                samples.Length,
                samples.Length / (float)SAMPLE_RATE,
                rms);
            return string.Empty;
        }

        var chunks = VoiceAudioChunkPlanner.Plan(samples.Length);
        var accepted = new List<string>();
        VoiceDiag("BATCH_TRANSCRIBE_START samples={0} seconds={1:F2} chunks={2} rms={3:F5}",
            samples.Length,
            samples.Length / (float)SAMPLE_RATE,
            chunks.Count,
            rms);

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = chunks[i];
            var segment = new float[chunk.Length];
            Array.Copy(samples, chunk.Start, segment, 0, chunk.Length);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _whisper.TranscribeAsync(segment, ct);
            sw.Stop();

            if (result is null || string.IsNullOrWhiteSpace(result.Text))
            {
                VoiceDiag("BATCH_CHUNK_EMPTY index={0} ms={1}", i, sw.ElapsedMilliseconds);
                continue;
            }

            if (!_guard.IsValidTranscription(result.Text, result.NoSpeechProb, result.Probability))
            {
                VoiceDiag("BATCH_CHUNK_REJECT index={0} \"{1}\" noSpeech={2:F2} logProb={3:F2}",
                    i, result.Text, result.NoSpeechProb, result.Probability);
                continue;
            }

            var text = result.Text.Trim();
            VoiceDiag("BATCH_CHUNK_ACCEPT index={0} chars={1} ms={2} noSpeech={3:F2}",
                i, text.Length, sw.ElapsedMilliseconds, result.NoSpeechProb);
            accepted.Add(text);
        }

        var merged = VoiceTextOverlapMerger.Merge(accepted);
        VoiceDiag("BATCH_TRANSCRIBE_DONE accepted={0} chars={1}", accepted.Count, merged.Length);
        return merged;
    }

    private async Task<(bool Success, string Message)> InjectPendingTextOnceAsync(string pendingText)
    {
        var normalized = NormalizeInjectedText(pendingText);
        if (normalized.Length == 0)
            return (true, "Nothing to append");

        if (Interlocked.Exchange(ref _injectInFlight, 1) == 1)
        {
            VoiceDiag("INJECT_SKIPPED already in flight");
            return (false, "Voice injection already in flight; skipped duplicate append.");
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastInjectedText, normalized, StringComparison.Ordinal)
                && now - _lastInjectedAt < TimeSpan.FromSeconds(20))
            {
                VoiceDiag("INJECT_SKIPPED duplicate chars={0}", normalized.Length);
                return (true, "Skipped duplicate dictation.");
            }

            var result = await _injector.AppendAsync(normalized + " ", _capturedTarget);
            if (result.Success)
            {
                _lastInjectedText = normalized;
                _lastInjectedAt = now;
            }

            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _injectInFlight, 0);
        }
    }

    private static string NormalizeInjectedText(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples)
            sum += s * (double)s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static void VoiceDiag(string fmt, params object[] args)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {string.Format(fmt, args)}";
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "TinyBoss", "voice_diag.log");
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    private static void PreserveTranscript(string text, string reason)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "TinyBoss");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "voice_failed_transcripts.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] reason={reason} chars={text.Length}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
            VoiceDiag("TRANSCRIPT_PRESERVED reason={0} chars={1}", reason, text.Length);
        }
        catch { }
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _hotKeyListener.VoiceKeyDown -= OnVoiceKeyDown;
        _hotKeyListener.VoiceKeyUp -= OnVoiceKeyUp;
        _audioCapture.SamplesAvailable -= OnSamplesAvailable;
        _hotKeyListener.Dispose();
        _audioCapture.Dispose();
    }

}

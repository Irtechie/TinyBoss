using System.Threading.Channels;
using TinyBoss.Platform.Windows;

namespace TinyBoss.Voice;

/// <summary>
/// Orchestrates voice-to-text using VAD + Whisper GPU:
/// HotKey down -> Start mic -> VAD detects speech/silence in real-time
/// On each silence gap -> speech chunk sent to Whisper GPU and buffered
/// HotKey up -> flush remaining audio through Whisper, drain queued chunks, then inject once
/// This preserves long dictation without sending synthetic input while the PTT key is held.
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

    // VAD state
    private readonly List<float> _sampleBuffer = new();     // All samples since key-down
    private readonly List<float> _preRollBuffer = new();     // Ring buffer for pre-speech audio
    private readonly object _vadLock = new();
    private int _speechStartIdx;          // Index in _sampleBuffer where current speech began
    private bool _inSpeech;
    private int _silenceSampleCount;      // How many consecutive silence samples
    private int _speechSampleCount;       // How many consecutive speech samples in current utterance
    private long _sessionToken;           // Monotonic session ID to discard stale results

    // Ordered transcription queue
    private Channel<SpeechSegment>? _segmentChannel;
    private Task? _consumerTask;
    private CancellationTokenSource? _sessionCts;
    private readonly VoiceTranscriptBuffer _transcriptBuffer = new();
    private int _stopInFlight;
    private int _injectInFlight;
    private string? _lastInjectedText;
    private DateTimeOffset _lastInjectedAt;

    // VAD tuning constants (16kHz sample rate)
    private const int SAMPLE_RATE = 16000;
    private const float SILENCE_DURATION_SEC = 0.7f;
    private const int SILENCE_SAMPLES = (int)(SAMPLE_RATE * SILENCE_DURATION_SEC);
    private const float PRE_ROLL_SEC = 0.3f;
    private const int PRE_ROLL_SAMPLES = (int)(SAMPLE_RATE * PRE_ROLL_SEC);
    private const float TAIL_PAD_SEC = 0.3f;
    private const int TAIL_PAD_SAMPLES = (int)(SAMPLE_RATE * TAIL_PAD_SEC);
    private const float SPEECH_THRESHOLD = 0.02f;          // Fixed RMS threshold for speech detection
    private const float MIN_SPEECH_SEC = 0.15f;         // Minimum speech to transcribe
    private const int MIN_SPEECH_SAMPLES = (int)(SAMPLE_RATE * MIN_SPEECH_SEC);
    private const float MAX_SEGMENT_SEC = 8.0f;
    private const int MAX_SEGMENT_SAMPLES = (int)(SAMPLE_RATE * MAX_SEGMENT_SEC);
    private static readonly TimeSpan FinalDrainTimeout = TimeSpan.FromSeconds(120);
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

        // Wire audio capture for VAD processing
        _audioCapture.SamplesAvailable += OnSamplesAvailable;
    }

    public void SetVoiceTarget(string? sessionId) => _voiceTargetSessionId = sessionId;

    public VoiceStatus GetVoiceStatus() => new(_recording);

    public void Start()
    {
        _hotKeyListener.Start();
        // Preload Whisper model on background thread so first use is instant
        _whisper.PreloadAsync();
        _logger.LogInformation("KH: Voice controller started (VAD + Whisper GPU)");
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

        lock (_vadLock)
        {
            _sampleBuffer.Clear();
            _preRollBuffer.Clear();
            _speechStartIdx = 0;
            _inSpeech = false;
            _silenceSampleCount = 0;
            _speechSampleCount = 0;
        }
        _transcriptBuffer.Clear();

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        var token = Interlocked.Increment(ref _sessionToken);
        _segmentChannel = Channel.CreateUnbounded<SpeechSegment>(
            new UnboundedChannelOptions { SingleReader = true });
        _consumerTask = Task.Run(() => ConsumeSegmentsAsync(token, _sessionCts.Token));

        _recording = true;
        RecordingStateChanged?.Invoke(true);
        VoiceDiag("RECORDING_STARTED (VAD + Whisper GPU)");
    }

    private void OnSamplesAvailable(float[] samples)
    {
        if (!_recording) return;

        lock (_vadLock)
        {
            _sampleBuffer.AddRange(samples);

            // VAD on the new chunk
            var chunkRms = CalculateRms(samples);

            if (!_inSpeech)
            {
                // Maintain pre-roll ring buffer
                _preRollBuffer.AddRange(samples);
                if (_preRollBuffer.Count > PRE_ROLL_SAMPLES)
                    _preRollBuffer.RemoveRange(0, _preRollBuffer.Count - PRE_ROLL_SAMPLES);

                if (chunkRms > SPEECH_THRESHOLD)
                {
                    // Speech started! Mark where it begins (include pre-roll)
                    _inSpeech = true;
                    _speechStartIdx = Math.Max(0, _sampleBuffer.Count - samples.Length - _preRollBuffer.Count + samples.Length);
                    _silenceSampleCount = 0;
                    _speechSampleCount = samples.Length;
                    VoiceDiag("SPEECH_START idx={0} rms={1:F5}", _speechStartIdx, chunkRms);
                }
            }
            else
            {
                _speechSampleCount += samples.Length;

                if (chunkRms <= SPEECH_THRESHOLD)
                {
                    _silenceSampleCount += samples.Length;

                    // Check if silence duration exceeded → end of phrase
                    if (_silenceSampleCount >= SILENCE_SAMPLES)
                    {
                        FlushSpeechSegment(includeTailPad: true);
                    }
                }
                else
                {
                    _silenceSampleCount = 0;
                }

                // Force flush if segment too long (continuous speech)
                if (_speechSampleCount >= MAX_SEGMENT_SAMPLES)
                {
                    VoiceDiag("MAX_SEGMENT forcing flush at {0:F1}s", _speechSampleCount / (float)SAMPLE_RATE);
                    FlushSpeechSegment(includeTailPad: false);
                }
            }
        }
    }

    /// <summary>
    /// Extract current speech segment, enqueue for Whisper, reset VAD state.
    /// Must be called under _vadLock.
    /// </summary>
    private void FlushSpeechSegment(bool includeTailPad)
    {
        var endIdx = _sampleBuffer.Count;
        if (includeTailPad)
        {
            // Include tail padding (silence after speech) but don't exceed buffer
            endIdx = Math.Min(_sampleBuffer.Count, _speechStartIdx + _speechSampleCount + TAIL_PAD_SAMPLES);
        }

        var segmentLength = endIdx - _speechStartIdx;
        if (segmentLength < MIN_SPEECH_SAMPLES)
        {
            VoiceDiag("SKIP_SHORT segment={0} samples ({1:F2}s)", segmentLength, segmentLength / (float)SAMPLE_RATE);
            ResetVadState();
            return;
        }

        // Copy segment to immutable array
        var segment = new float[segmentLength];
        _sampleBuffer.CopyTo(_speechStartIdx, segment, 0, segmentLength);

        VoiceDiag("ENQUEUE segment={0:F2}s ({1} samples)", segmentLength / (float)SAMPLE_RATE, segmentLength);

        _segmentChannel?.Writer.TryWrite(new SpeechSegment(segment, Interlocked.Read(ref _sessionToken)));
        ResetVadState();
    }

    private void ResetVadState()
    {
        _inSpeech = false;
        _silenceSampleCount = 0;
        _speechSampleCount = 0;
        _preRollBuffer.Clear();
        _sampleBuffer.Clear();
    }

    /// <summary>
    /// Background consumer: transcribes segments in order and buffers accepted text.
    /// </summary>
    private async Task ConsumeSegmentsAsync(long expectedToken, CancellationToken ct)
    {
        try
        {
            await foreach (var segment in _segmentChannel!.Reader.ReadAllAsync(ct))
            {
                if (segment.SessionToken != expectedToken) continue; // Stale segment

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await _whisper.TranscribeAsync(segment.Samples, ct);
                    sw.Stop();

                    if (result is null || string.IsNullOrWhiteSpace(result.Text))
                    {
                        VoiceDiag("WHISPER_EMPTY ({0}ms)", sw.ElapsedMilliseconds);
                        continue;
                    }

                    // Check hallucination guard
                    if (!_guard.IsValidTranscription(result.Text, result.NoSpeechProb, result.Probability))
                    {
                        VoiceDiag("GUARD_REJECT \"{0}\" noSpeech={1:F2} logProb={2:F2}",
                            result.Text, result.NoSpeechProb, result.Probability);
                        continue;
                    }

                    var text = result.Text.Trim();
                    VoiceDiag("WHISPER \"{0}\" ({1}ms, noSpeech={2:F2})",
                        text, sw.ElapsedMilliseconds, result.NoSpeechProb);

                    await AppendRecognizedTextAsync(text, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    VoiceDiag("TRANSCRIBE_ERROR {0}: {1}", ex.GetType().Name, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            VoiceDiag("CONSUMER_ERROR {0}: {1}", ex.GetType().Name, ex.Message);
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

        _recording = false;
        RecordingStateChanged?.Invoke(false);

        _audioCapture.Stop();
        _audioCapture.RetainRawAudio = true;

        lock (_vadLock)
        {
            if (_inSpeech && _speechSampleCount >= MIN_SPEECH_SAMPLES)
                FlushSpeechSegment(includeTailPad: false);
        }

        _segmentChannel?.Writer.TryComplete();

        try
        {
            if (_consumerTask is not null)
                await _consumerTask.WaitAsync(FinalDrainTimeout, ct);
        }
        catch (TimeoutException)
        {
            VoiceDiag("DRAIN_TIMEOUT after {0:F0}s", FinalDrainTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            VoiceDiag("DRAIN_CANCELLED");
            return (false, string.Empty, "Dictation cancelled.");
        }
        catch (Exception ex)
        {
            VoiceDiag("DRAIN_ERROR {0}: {1}", ex.GetType().Name, ex.Message);
        }

        if (KeyUpSettleDelay > TimeSpan.Zero)
            await Task.Delay(KeyUpSettleDelay, ct);

        var pendingText = _transcriptBuffer.Flush();
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

    private async Task AppendRecognizedTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var dangerousMatch = _guard.CheckDestructiveCommand(text);
        if (dangerousMatch is not null)
        {
            VoiceDiag("DESTRUCTIVE_BLOCK \"{0}\" in \"{1}\"", dangerousMatch, text);
            StatusMessage?.Invoke($"Blocked dangerous command: {dangerousMatch}");
            return;
        }

        try
        {
            if (!_transcriptBuffer.Add(text))
                return;

            VoiceDiag("BUFFER_APPEND chars={0} segments={1} totalChars={2}",
                text.Length, _transcriptBuffer.SegmentCount, _transcriptBuffer.CharacterCount);
            await Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VoiceDiag("BUFFER_ERROR {0}: {1}", ex.GetType().Name, ex.Message);
            StatusMessage?.Invoke($"Voice buffer error: {ex.Message}");
        }
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

            var result = await _injector.AppendAsync(normalized + " ", _voiceTargetSessionId);
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

    private static float CalculateRms(IReadOnlyList<float> samples, int offset = 0, int count = -1)
    {
        if (count < 0) count = samples.Count - offset;
        if (count <= 0) return 0;

        double sum = 0;
        for (int i = offset; i < offset + count; i++)
            sum += samples[i] * (double)samples[i];

        return (float)Math.Sqrt(sum / count);
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
        _segmentChannel?.Writer.TryComplete();
        try { _consumerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _sessionCts?.Dispose();
        _hotKeyListener.VoiceKeyDown -= OnVoiceKeyDown;
        _hotKeyListener.VoiceKeyUp -= OnVoiceKeyUp;
        _audioCapture.SamplesAvailable -= OnSamplesAvailable;
        _hotKeyListener.Dispose();
        _audioCapture.Dispose();
    }

    private sealed record SpeechSegment(float[] Samples, long SessionToken);
}

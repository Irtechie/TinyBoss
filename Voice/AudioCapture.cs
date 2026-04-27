using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TinyBoss.Voice;

/// <summary>
/// Captures microphone audio via WASAPI shared mode, resamples to 16kHz mono float32
/// for Whisper.net consumption. Re-acquires device on each recording start (Bluetooth hot-swap).
/// Raw bytes are collected during recording; conversion happens once on Stop().
/// Supports real-time sample callbacks for streaming STT (Sherpa-ONNX).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly ILogger<AudioCapture> _logger;
    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private readonly List<byte> _rawBytes = new();
    private readonly object _lock = new();
    private bool _recording;

    /// <summary>
    /// Keep native mic bytes for Stop()/SnapshotSamples(). Streaming VAD callers can
    /// disable this so long dictation sessions do not retain all raw audio.
    /// </summary>
    public bool RetainRawAudio { get; set; } = true;

    /// <summary>
    /// Real-time callback for streaming: fires with 16kHz mono float32 samples
    /// as they arrive from the mic. Subscribe before calling Start().
    /// </summary>
    public event Action<float[]>? SamplesAvailable;

    public bool IsRecording => _recording;

    public AudioCapture(ILogger<AudioCapture> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start capturing audio. Returns false if no audio device is available.
    /// </summary>
    public bool Start()
    {
        if (_recording) return true;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _capture = new WasapiCapture(device);
            _captureFormat = _capture.WaveFormat;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            lock (_lock) _rawBytes.Clear();
            _recording = true;
            _capture.StartRecording();

            _logger.LogDebug("KH: Audio capture started ({Format})", _captureFormat);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KH: Failed to start audio capture — no mic?");
            CleanupCapture();
            return false;
        }
    }

    /// <summary>
    /// Stop capturing and return the accumulated 16kHz mono float32 samples.
    /// Conversion from native format happens here (not during recording).
    /// </summary>
    public float[] Stop()
    {
        if (!_recording) return [];

        _recording = false;

        // StopRecording() THEN Dispose() — reverse order causes AccessViolationException
        try { _capture?.StopRecording(); } catch { }

        float[] result;
        lock (_lock)
        {
            if (_rawBytes.Count == 0 || _captureFormat is null)
            {
                _rawBytes.Clear();
                CleanupCapture();
                return [];
            }

            result = ConvertToFloat16kMono(_rawBytes.ToArray(), _captureFormat);
            _rawBytes.Clear();
        }

        CleanupCapture();
        _logger.LogDebug("KH: Audio capture stopped, {N} samples collected ({Sec:F1}s)",
            result.Length, result.Length / 16000.0);
        return result;
    }

    /// <summary>
    /// Snapshot current audio without stopping recording (for chunked streaming).
    /// Returns 16kHz mono float32 samples accumulated so far.
    /// </summary>
    public float[] SnapshotSamples()
    {
        byte[] rawCopy;
        WaveFormat format;
        lock (_lock)
        {
            if (_rawBytes.Count == 0 || _captureFormat is null) return [];
            rawCopy = _rawBytes.ToArray();
            format = _captureFormat;
        }
        return ConvertToFloat16kMono(rawCopy, format);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        if (RetainRawAudio)
        {
            lock (_lock)
            {
                _rawBytes.AddRange(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
            }
        }

        // Fire real-time streaming callback if anyone is listening
        if (SamplesAvailable is not null && _captureFormat is not null)
        {
            try
            {
                var chunk = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
                var samples = ConvertToFloat16kMono(chunk, _captureFormat);
                if (samples.Length > 0)
                    SamplesAvailable?.Invoke(samples);
            }
            catch { /* Don't let callback errors kill audio capture */ }
        }
    }

    /// <summary>
    /// Convert raw captured bytes to 16kHz mono float32 in a single pass
    /// from a finite MemoryStream (no risk of infinite loop).
    /// </summary>
    private static float[] ConvertToFloat16kMono(byte[] raw, WaveFormat format)
    {
        using var ms = new MemoryStream(raw);
        var rawWave = new RawSourceWaveStream(ms, format);
        ISampleProvider pipeline = rawWave.ToSampleProvider();

        if (format.Channels > 1)
            pipeline = new StereoToMonoSampleProvider(pipeline);

        if (format.SampleRate != 16000)
            pipeline = new WdlResamplingSampleProvider(pipeline, 16000);

        var samples = new List<float>(raw.Length / (format.BitsPerSample / 8 * format.Channels));
        var buffer = new float[4096];
        int read;
        while ((read = pipeline.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }
        return samples.ToArray();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogWarning(e.Exception, "KH: Audio recording stopped with error");
    }

    private void CleanupCapture()
    {
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _captureFormat = null;
    }

    public void Dispose() => CleanupCapture();
}

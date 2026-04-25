using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KittenHerder.Voice;

/// <summary>
/// Captures microphone audio via WASAPI shared mode, resamples to 16kHz mono float32
/// for Whisper.net consumption. Re-acquires device on each recording start (Bluetooth hot-swap).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly ILogger<AudioCapture> _logger;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private readonly List<float> _samples = new();
    private readonly object _lock = new();
    private bool _recording;

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
            // Never set WasapiCapture.WaveFormat directly — must use device native format
            _buffered = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            lock (_lock) _samples.Clear();
            _recording = true;
            _capture.StartRecording();

            _logger.LogDebug("KH: Audio capture started ({Format})", _capture.WaveFormat);
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
    /// </summary>
    public float[] Stop()
    {
        if (!_recording) return [];

        _recording = false;

        // StopRecording() THEN Dispose() — reverse order causes AccessViolationException
        try { _capture?.StopRecording(); } catch { }

        // Drain any remaining buffered data
        DrainBuffer();

        CleanupCapture();

        lock (_lock)
        {
            var result = _samples.ToArray();
            _samples.Clear();
            _logger.LogDebug("KH: Audio capture stopped, {N} samples collected ({Sec:F1}s)",
                result.Length, result.Length / 16000.0);
            return result;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffered?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        DrainBuffer();
    }

    private void DrainBuffer()
    {
        if (_buffered is null) return;

        // Build resampling pipeline: native → mono → 16kHz → float32
        ISampleProvider pipeline = _buffered.ToSampleProvider();

        if (_buffered.WaveFormat.Channels > 1)
            pipeline = new StereoToMonoSampleProvider(pipeline);

        if (_buffered.WaveFormat.SampleRate != 16000)
            pipeline = new WdlResamplingSampleProvider(pipeline, 16000);

        var buffer = new float[4096];
        int read;
        while ((read = pipeline.Read(buffer, 0, buffer.Length)) > 0)
        {
            lock (_lock)
            {
                for (int i = 0; i < read; i++)
                    _samples.Add(buffer[i]);
            }
        }
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
        _buffered = null;
    }

    public void Dispose() => CleanupCapture();
}

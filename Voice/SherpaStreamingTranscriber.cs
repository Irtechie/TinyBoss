using SherpaOnnx;

namespace TinyBoss.Voice;

/// <summary>
/// Real-time streaming speech-to-text using Sherpa-ONNX OnlineRecognizer.
/// Unlike Whisper (batch), this processes audio incrementally — text grows
/// word-by-word as you speak. Built-in endpoint detection commits sentences on pauses.
/// </summary>
public sealed class SherpaStreamingTranscriber : IDisposable
{
    private readonly ILogger<SherpaStreamingTranscriber> _logger;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private readonly object _lock = new();
    private bool _initialized;

    public bool IsReady => _initialized;

    public SherpaStreamingTranscriber(ILogger<SherpaStreamingTranscriber> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the recognizer with the streaming zipformer model.
    /// Call once at startup. Safe to call multiple times.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TinyBoss", "models", "sherpa");

        var encoder = Path.Combine(modelDir, "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        var decoder = Path.Combine(modelDir, "decoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        var joiner = Path.Combine(modelDir, "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx");
        var tokens = Path.Combine(modelDir, "tokens-en.txt");

        if (!File.Exists(encoder))
        {
            _logger.LogWarning("Sherpa model not found at {Dir}. Streaming STT unavailable.", modelDir);
            return;
        }

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;

        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;

        // Try CUDA first, fall back to CPU
        config.ModelConfig.Provider = "cpu";

        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;

        // Endpoint detection: commit text when silence is detected
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 3.0f;   // Long silence = endpoint even with no speech
        config.Rule2MinTrailingSilence = 1.2f;   // Silence after speech = endpoint (was 0.8, too aggressive)
        config.Rule3MinUtteranceLength = 120.0f; // Max utterance length if this path is enabled later

        _recognizer = new OnlineRecognizer(config);
        _initialized = true;
        _logger.LogInformation("Sherpa streaming recognizer initialized (CPU)");
    }

    /// <summary>
    /// Create a new recognition stream for a new recording session.
    /// </summary>
    public void StartStream()
    {
        lock (_lock)
        {
            _stream = _recognizer?.CreateStream();
        }
    }

    /// <summary>
    /// Push audio samples (16kHz mono float32) into the stream.
    /// Called from the audio capture callback as data arrives.
    /// </summary>
    public void AcceptWaveform(float[] samples)
    {
        lock (_lock)
        {
            _stream?.AcceptWaveform(16000, samples);
        }
    }

    /// <summary>
    /// Decode any pending audio and return current partial text.
    /// Also returns whether an endpoint (pause) was detected.
    /// </summary>
    public (string Text, bool IsEndpoint) GetPartialResult()
    {
        lock (_lock)
        {
            if (_recognizer is null || _stream is null)
                return ("", false);

            while (_recognizer.IsReady(_stream))
                _recognizer.Decode(_stream);

            var text = _recognizer.GetResult(_stream).Text ?? "";
            var isEndpoint = _recognizer.IsEndpoint(_stream);

            return (NormalizeCase(text.Trim()), isEndpoint);
        }
    }

    /// <summary>
    /// Reset the stream after an endpoint is committed.
    /// Keeps the recognizer alive for the next utterance.
    /// </summary>
    public void ResetStream()
    {
        lock (_lock)
        {
            if (_recognizer is not null && _stream is not null)
                _recognizer.Reset(_stream);
        }
    }

    /// <summary>
    /// Signal input is finished and decode any remaining audio.
    /// </summary>
    public string FinishStream()
    {
        lock (_lock)
        {
            if (_recognizer is null || _stream is null)
                return "";

            _stream.InputFinished();

            while (_recognizer.IsReady(_stream))
                _recognizer.Decode(_stream);

            var text = _recognizer.GetResult(_stream).Text ?? "";
            return NormalizeCase(text.Trim());
        }
    }

    /// <summary>
    /// Sherpa BPE models output ALL CAPS. Convert to sentence case.
    /// </summary>
    private static string NormalizeCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Lowercase everything, then capitalize first letter and after sentence-ending punctuation
        var lower = text.ToLowerInvariant();
        var chars = lower.ToCharArray();
        bool capitalizeNext = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (capitalizeNext && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                capitalizeNext = false;
            }
            if (chars[i] == '.' || chars[i] == '!' || chars[i] == '?')
                capitalizeNext = true;
        }
        return new string(chars);
    }

    public void Dispose()
    {
        _stream = null;
        _recognizer?.Dispose();
        _recognizer = null;
    }
}

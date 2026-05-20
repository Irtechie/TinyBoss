using Whisper.net;
using Whisper.net.Ggml;
using TinyBoss.Core;

namespace TinyBoss.Voice;

/// <summary>
/// Manages Whisper.net model lifecycle with lazy loading, single Factory/Processor reuse,
/// and keeps it loaded for the process lifetime. Native Whisper/CUDA unload
/// and reload has caused process-level crashes on long-lived TinyBoss sessions.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly ILogger<WhisperTranscriber> _logger;
    private readonly TinyBossConfig _config;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _loadedModelId;
    private string? _loadedModelPath;
    private readonly string _diagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TinyBoss", "voice_diag.log");

    public bool IsModelLoaded => _processor is not null;
    public string SelectedModel => WhisperModelCatalog.Resolve(_config.WhisperModel).Id;
    public string ActiveModel => _loadedModelId ?? string.Empty;
    public string ActiveModelPath => _loadedModelPath ?? string.Empty;

    public WhisperTranscriber(ILogger<WhisperTranscriber> logger, TinyBossConfig config)
    {
        _logger = logger;
        _config = config;
        Directory.CreateDirectory(_config.ModelDir);
    }

    /// <summary>
    /// Preload the model on a background thread so first voice use is instant.
    /// </summary>
    public void PreloadAsync()
    {
        VoiceDiag("WHISPER_PRELOAD_START");
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync();
                try
                {
                    await EnsureModelLoadedAsync(CancellationToken.None);
                    await WarmUpProcessorAsync(CancellationToken.None);
                }
                finally { _gate.Release(); }
                _logger.LogInformation("KH: Whisper model preloaded and ready");
                VoiceDiag("WHISPER_PRELOAD_READY");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KH: Whisper model preload failed (will retry on first use)");
                VoiceDiag("WHISPER_PRELOAD_FAIL {0}: {1}", ex.GetType().Name, ex.Message);
            }
        });
    }

    /// <summary>
    /// Transcribe 16kHz mono float32 audio. Lazy-loads model on first call.
    /// Returns (text, noSpeechProb, avgLogProb) or null if no segments produced.
    /// </summary>
    public async Task<TranscriptionResult?> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureModelLoadedAsync(ct);

            if (_processor is null)
                return null;

            var segments = new List<SegmentData>();
            VoiceDiag("WHISPER_PROCESS_START samples={0} seconds={1:F2}", samples.Length, samples.Length / 16000.0);
            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                segments.Add(segment);
            }
            VoiceDiag("WHISPER_PROCESS_DONE segments={0}", segments.Count);

            if (segments.Count == 0)
                return null;

            // Combine all segment text
            var text = string.Join(" ", segments.Select(s => s.Text)).Trim();
            var noSpeech = segments.Average(s => s.NoSpeechProbability);
            var avgLogProb = segments.Average(s => s.Probability);

            _logger.LogDebug("KH: Whisper transcribed: \"{Text}\" (noSpeech={NS:F2}, avgLogProb={ALP:F2})",
                text, noSpeech, avgLogProb);

            return new TranscriptionResult(text, (float)noSpeech, (float)avgLogProb);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureModelLoadedAsync(CancellationToken ct)
    {
        var spec = WhisperModelCatalog.Resolve(_config.WhisperModel);
        var modelDir = string.IsNullOrWhiteSpace(_config.ModelDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyBoss", "models")
            : _config.ModelDir;
        var modelPath = Path.Combine(modelDir, spec.FileName);

        if (_processor is not null && string.Equals(_loadedModelId, spec.Id, StringComparison.OrdinalIgnoreCase))
            return;

        if (_processor is not null)
        {
            VoiceDiag("WHISPER_MODEL_SWITCH from={0} to={1}", _loadedModelId ?? "unknown", spec.Id);
            DisposeLoadedModel();
        }

        Directory.CreateDirectory(modelDir);
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < spec.MinBytes)
        {
            _logger.LogInformation("KH: Downloading Whisper model {Model}...", spec.Id);
            await DownloadModelAsync(spec, modelPath, ct);
        }

        _logger.LogInformation("KH: Loading Whisper model {Model} from {Path}", spec.Id, modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithSingleSegment()
            .WithNoSpeechThreshold(0.4f)
            .WithThreads(4)
            .Build();
        _loadedModelId = spec.Id;
        _loadedModelPath = modelPath;

        _logger.LogInformation("KH: Whisper model {Model} loaded and ready", spec.Id);
        VoiceDiag("WHISPER_MODEL_READY id={0} path=\"{1}\"", spec.Id, modelPath);
    }

    private async Task WarmUpProcessorAsync(CancellationToken ct)
    {
        if (_processor is null) return;

        try
        {
            var silence = new float[16000 / 4];
            await foreach (var _ in _processor.ProcessAsync(silence, ct))
            {
            }

            _logger.LogInformation("KH: Whisper processor warmed up");
            VoiceDiag("WHISPER_WARM_READY");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "KH: Whisper processor warm-up failed; first transcription will warm it");
            VoiceDiag("WHISPER_WARM_FAIL {0}: {1}", ex.GetType().Name, ex.Message);
        }
    }

    public async Task ReloadConfiguredModelAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DisposeLoadedModel();
            await EnsureModelLoadedAsync(ct);
            await WarmUpProcessorAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadModelAsync(WhisperModelSpec spec, string modelPath, CancellationToken ct)
    {
        var tmpPath = modelPath + ".tmp";
        try
        {
            using var httpClient = new HttpClient();
            var downloader = new WhisperGgmlDownloader(httpClient);
            using var modelStream = await downloader.GetGgmlModelAsync(
                spec.GgmlType, QuantizationType.NoQuantization, ct);
            using var fileStream = File.Create(tmpPath);
            await modelStream.CopyToAsync(fileStream, ct);
            fileStream.Close();

            File.Move(tmpPath, modelPath, overwrite: true);
            _logger.LogInformation("KH: Whisper model {Model} downloaded to {Path}", spec.Id, modelPath);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private void DisposeLoadedModel()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _loadedModelId = null;
        _loadedModelPath = null;
    }

    private void VoiceDiag(string fmt, params object[] args)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {string.Format(fmt, args)}";
            File.AppendAllText(_diagPath, line + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        DisposeLoadedModel();
        _gate.Dispose();
    }
}

public sealed record TranscriptionResult(string Text, float NoSpeechProb, float Probability);

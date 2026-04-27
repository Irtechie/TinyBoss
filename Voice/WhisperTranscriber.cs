using Whisper.net;
using Whisper.net.Ggml;

namespace TinyBoss.Voice;

/// <summary>
/// Manages Whisper.net model lifecycle with lazy loading, single Factory/Processor reuse,
/// and 60-second idle unload to reclaim ~300MB of RAM.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly ILogger<WhisperTranscriber> _logger;
    private readonly string _modelDir;
    private readonly string _modelPath;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _idleTimer;
    private const int IdleTimeoutMs = 30 * 60_000;
    private readonly string _diagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "TinyBoss", "voice_diag.log");

    public bool IsModelLoaded => _processor is not null;

    public WhisperTranscriber(ILogger<WhisperTranscriber> logger)
    {
        _logger = logger;
        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TinyBoss", "models");
        _modelPath = Path.Combine(_modelDir, "ggml-tiny.en.bin");
        Directory.CreateDirectory(_modelDir);
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
                ResetIdleTimer();
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
            ResetIdleTimer();
            await EnsureModelLoadedAsync(ct);

            if (_processor is null)
                return null;

            var segments = new List<SegmentData>();
            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                segments.Add(segment);
            }

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
        if (_processor is not null) return;

        if (!File.Exists(_modelPath))
        {
            _logger.LogInformation("KH: Downloading Whisper model (ggml-base.en, ~74 MB)...");
            await DownloadModelAsync(ct);
        }

        _logger.LogInformation("KH: Loading Whisper model from {Path}", _modelPath);
        _factory = WhisperFactory.FromPath(_modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithSingleSegment()
            .WithNoSpeechThreshold(0.4f)
            .WithThreads(4)
            .Build();

        _logger.LogInformation("KH: Whisper model loaded and ready");
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

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        var tmpPath = _modelPath + ".tmp";
        try
        {
            using var httpClient = new HttpClient();
            var downloader = new WhisperGgmlDownloader(httpClient);
            using var modelStream = await downloader.GetGgmlModelAsync(
                GgmlType.Base, QuantizationType.NoQuantization, ct);
            using var fileStream = File.Create(tmpPath);
            await modelStream.CopyToAsync(fileStream, ct);
            fileStream.Close();

            File.Move(tmpPath, _modelPath, overwrite: true);
            _logger.LogInformation("KH: Whisper model downloaded to {Path}", _modelPath);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private void ResetIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = new Timer(_ => UnloadModel(), null, IdleTimeoutMs, Timeout.Infinite);
    }

    private void UnloadModel()
    {
        if (!_gate.Wait(0)) return; // Someone is using it
        try
        {
            if (_processor is null) return;

            _processor.Dispose();
            _processor = null;
            _factory?.Dispose();
            _factory = null;

            GC.Collect(2, GCCollectionMode.Aggressive, true);

            _logger.LogInformation("KH: Whisper model unloaded after idle timeout (~300 MB reclaimed)");
            VoiceDiag("WHISPER_UNLOADED_IDLE");
        }
        finally
        {
            _gate.Release();
        }
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
        _idleTimer?.Dispose();
        _processor?.Dispose();
        _factory?.Dispose();
        _gate.Dispose();
    }
}

public sealed record TranscriptionResult(string Text, float NoSpeechProb, float Probability);

using Whisper.net.Ggml;

namespace TinyBoss.Voice;

public sealed record WhisperModelSpec(
    string Id,
    string DisplayName,
    string FileName,
    GgmlType GgmlType,
    long MinBytes);

public static class WhisperModelCatalog
{
    public const string DefaultModelId = "tiny.en";

    public static readonly IReadOnlyList<WhisperModelSpec> All =
    [
        new("tiny.en", "Tiny English - fastest", "ggml-tiny.en.bin", GgmlType.Tiny, 70L * 1024 * 1024),
        new("base.en", "Base English - balanced", "ggml-base.en.bin", GgmlType.Base, 140L * 1024 * 1024),
        new("small.en", "Small English - higher accuracy", "ggml-small.en.bin", GgmlType.Small, 450L * 1024 * 1024),
    ];

    public static WhisperModelSpec Resolve(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(m => m.Id == DefaultModelId);
}

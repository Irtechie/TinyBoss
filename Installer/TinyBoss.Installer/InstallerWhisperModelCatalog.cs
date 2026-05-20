using Whisper.net.Ggml;

namespace TinyBoss.Installer;

public sealed record InstallerWhisperModel(
    string Id,
    string FileName,
    GgmlType GgmlType,
    long MinBytes);

public static class InstallerWhisperModelCatalog
{
    private const string DefaultModelId = "tiny.en";

    private static readonly IReadOnlyList<InstallerWhisperModel> All =
    [
        new("tiny.en", "ggml-tiny.en.bin", GgmlType.Tiny, 70L * 1024 * 1024),
        new("base.en", "ggml-base.en.bin", GgmlType.Base, 140L * 1024 * 1024),
        new("small.en", "ggml-small.en.bin", GgmlType.Small, 450L * 1024 * 1024),
    ];

    public static InstallerWhisperModel Resolve(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(m => m.Id == DefaultModelId);
}

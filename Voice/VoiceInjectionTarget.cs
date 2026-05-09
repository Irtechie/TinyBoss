namespace TinyBoss.Voice;

public sealed record VoiceInjectionTarget(
    string? SessionId,
    nint WindowHandle,
    string Description);

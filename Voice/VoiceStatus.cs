namespace TinyBoss.Voice;

public sealed record VoiceStatus(
    bool Recording,
    string SelectedModel,
    string ActiveModel,
    bool ModelLoaded);

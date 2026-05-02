namespace TinyBoss.Core;

public static class TinyBossSessionKind
{
    public const string ExternalWindow = "external_window";
    public const string RedirectedProcess = "redirected_process";
    public const string BossTerminalPty = "boss_terminal_pty";
}

public static class TinyBossLaneState
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Unknown = "unknown";
}

public static class TinyBossCapability
{
    public const string SendText = "send_text";
    public const string StreamTranscript = "stream_transcript";
    public const string Interrupt = "interrupt";
    public const string Kill = "kill";
    public const string WindowInject = "window_inject";
    public const string SetTitle = "set_title";
    public const string Resize = "resize";
}

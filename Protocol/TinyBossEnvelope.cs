using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyBoss.Protocol;

/// <summary>
/// Wire envelope for all TinyBoss ↔ PitBoss messages.
/// Intentionally separate from SynapseEnvelope — independently versionable.
/// </summary>
public sealed class KhEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Null for broadcast messages (introspect, hello).</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>Known message type strings.</summary>
public static class KhMessageType
{
    // PitBoss → KH
    public const string Hello       = "hello";
    public const string Spawn       = "spawn";
    public const string Inject      = "inject";
    public const string WindowInject = "window_inject";
    public const string Kill        = "kill";
    public const string Signal      = "signal";
    public const string Introspect  = "introspect";
    public const string AnswerUser  = "answer_user";
    public const string Rename      = "rename";

    // KH → Logos
    public const string HelloAck        = "hello_ack";
    public const string SpawnAck        = "spawn_ack";
    public const string Ack             = "ack";
    public const string Error           = "error";
    public const string StreamOut       = "stream_out";
    public const string Report          = "report";
    public const string IntrospectReply = "introspect_reply";
}

// ── Payload shapes ──────────────────────────────────────────────────────────

public sealed record SpawnPayload(
    [property: JsonPropertyName("executable")]    string Executable,
    [property: JsonPropertyName("command")]       string? Command,    // legacy alias
    [property: JsonPropertyName("args")]          string[]? Args,
    [property: JsonPropertyName("cwd")]           string? Cwd,
    [property: JsonPropertyName("source_surface")] string SourceSurface,
    [property: JsonPropertyName("visible")]       bool Visible = true,  // open a real window by default
    [property: JsonPropertyName("title")]         string? Title = null  // window title, e.g. "Inferno - Tony - 1"
);

public sealed record InjectPayload(
    [property: JsonPropertyName("text")] string Text
);

public sealed record WindowInjectPayload(
    [property: JsonPropertyName("text")]           string Text,
    [property: JsonPropertyName("hwnd")]           string? Hwnd,
    [property: JsonPropertyName("slot")]           int? Slot,
    [property: JsonPropertyName("monitor_handle")] string? MonitorHandle,
    [property: JsonPropertyName("source_surface")] string? SourceSurface
);

public sealed record AnswerUserPayload(
    [property: JsonPropertyName("text")] string Text
);

public sealed record SignalPayload(
    [property: JsonPropertyName("signal")] string Signal   // "ctrl_c" | "ctrl_break"
);

public sealed record StreamOutPayload(
    [property: JsonPropertyName("lines")] string[] Lines,
    [property: JsonPropertyName("fd")]    string Fd,       // "stdout" | "stderr"
    [property: JsonPropertyName("ts")]    long TimestampMs
);

public sealed record ReportPayload(
    [property: JsonPropertyName("exit_code")]  int? ExitCode,
    [property: JsonPropertyName("reason")]     string Reason  // "exited" | "killed" | "error"
);

public sealed record AckPayload(
    [property: JsonPropertyName("ok")]      bool Ok,
    [property: JsonPropertyName("message")] string? Message = null
);

public sealed record SessionInfo(
    [property: JsonPropertyName("session_id")]     string SessionId,
    [property: JsonPropertyName("pid")]            int Pid,
    [property: JsonPropertyName("start_time")]     string StartTime,   // ISO 8601
    [property: JsonPropertyName("command")]        string Command,
    [property: JsonPropertyName("cwd")]            string? Cwd,
    [property: JsonPropertyName("source_surface")] string SourceSurface,
    [property: JsonPropertyName("running")]        bool Running
);

public sealed record TiledWindowInfo(
    [property: JsonPropertyName("device_name")]    string DeviceName,
    [property: JsonPropertyName("monitor_handle")] string MonitorHandle,
    [property: JsonPropertyName("slot")]           int Slot,
    [property: JsonPropertyName("hwnd")]           string Hwnd,
    [property: JsonPropertyName("pid")]            int Pid,
    [property: JsonPropertyName("session_id")]     string? SessionId,
    [property: JsonPropertyName("alias")]          string? Alias,
    [property: JsonPropertyName("title")]          string Title,
    [property: JsonPropertyName("running")]        bool Running,
    [property: JsonPropertyName("text_tail")]      string[] TextTail,
    [property: JsonPropertyName("captured_at")]    string? CapturedAt
);

public sealed record IntrospectReplyPayload(
    [property: JsonPropertyName("sessions")]      SessionInfo[] Sessions,
    [property: JsonPropertyName("tiled_windows")] TiledWindowInfo[] TiledWindows
);

public sealed record RenamePayload(
    [property: JsonPropertyName("slot")]           int? Slot,
    [property: JsonPropertyName("alias")]          string Alias,
    [property: JsonPropertyName("hwnd")]           string? Hwnd = null,
    [property: JsonPropertyName("monitor_handle")] string? MonitorHandle = null
);

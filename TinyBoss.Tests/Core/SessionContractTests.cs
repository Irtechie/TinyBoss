using System.Text.Json;
using TinyBoss.Core;
using TinyBoss.Protocol;
using Xunit;

namespace TinyBoss.Tests.Core;

public sealed class SessionContractTests
{
    [Fact]
    public void SessionInfo_DefaultsRemainBackwardCompatible()
    {
        var session = new SessionInfo(
            SessionId: "session-1",
            Pid: 123,
            StartTime: "2026-05-01T00:00:00Z",
            Command: "pwsh.exe",
            Cwd: "E:\\Dev",
            SourceSurface: "pitboss",
            Running: true);

        Assert.Equal(TinyBossSessionKind.RedirectedProcess, session.SessionKind);
        Assert.Equal(TinyBossLaneState.Running, session.State);
        Assert.True(session.SupportsTranscriptTail);
    }

    [Fact]
    public void SessionInfo_SerializesCapabilityContract()
    {
        var session = new SessionInfo(
            SessionId: "session-1",
            Pid: 123,
            StartTime: "2026-05-01T00:00:00Z",
            Command: "pwsh.exe",
            Cwd: "E:\\Dev",
            SourceSurface: "pitboss",
            Running: true,
            SessionKind: TinyBossSessionKind.RedirectedProcess,
            Capabilities: [TinyBossCapability.SendText, TinyBossCapability.StreamTranscript],
            State: TinyBossLaneState.Running,
            RawStreamAvailable: true,
            SupportsTranscriptTail: true);

        var json = JsonSerializer.Serialize(session);

        Assert.Contains("\"session_kind\":\"redirected_process\"", json);
        Assert.Contains("\"capabilities\":[\"send_text\",\"stream_transcript\"]", json);
        Assert.Contains("\"raw_stream_available\":true", json);
    }

    [Fact]
    public void TiledWindowInfo_DescribesExternalWindowFallback()
    {
        var window = new TiledWindowInfo(
            DeviceName: "\\\\.\\DISPLAY1",
            MonitorHandle: "123",
            Slot: 0,
            Hwnd: "456",
            Pid: 789,
            SessionId: null,
            Alias: "Copilot 2",
            Title: "Copilot",
            Running: true,
            TextTail: ["ready"],
            CapturedAt: "2026-05-01T00:00:00Z",
            SessionKind: TinyBossSessionKind.ExternalWindow,
            Capabilities: [TinyBossCapability.WindowInject, TinyBossCapability.SetTitle],
            State: TinyBossLaneState.Running);

        Assert.Equal(TinyBossSessionKind.ExternalWindow, window.SessionKind);
        Assert.Contains(TinyBossCapability.WindowInject, window.Capabilities!);
        Assert.False(window.RawStreamAvailable);
    }
}

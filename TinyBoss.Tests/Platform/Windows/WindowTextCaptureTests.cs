using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class WindowTextCaptureTests
{
    [Fact]
    public void IsUsefulCapturedText_RejectsWindowTitleOnly()
    {
        Assert.False(WindowTextCapture.IsUsefulCapturedText(
            "Executing slice-002",
            "Executing slice-002"));
    }

    [Fact]
    public void IsUsefulCapturedText_AcceptsTranscriptWithTitleLine()
    {
        Assert.True(WindowTextCapture.IsUsefulCapturedText(
            """
            Executing slice-002
            dotnet test
            Passed! - Failed: 0, Passed: 42
            """,
            "Executing slice-002"));
    }
}

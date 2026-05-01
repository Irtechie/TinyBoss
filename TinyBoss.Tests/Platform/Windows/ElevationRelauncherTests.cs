using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class ElevationRelauncherTests
{
    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    public void ShouldAttemptRelaunchOnlyForLimitedWindowsProcessWithTaskAndNoRecentMarker(
        bool isWindows,
        bool isElevated,
        bool taskExists,
        bool recentMarker,
        bool expected)
    {
        var actual = ElevationRelauncher.ShouldAttemptRelaunch(
            isWindows,
            isElevated,
            taskExists,
            recentMarker);

        Assert.Equal(expected, actual);
    }
}

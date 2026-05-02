using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class MonitorEnumeratorTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY1", "DISPLAY1")]
    [InlineData(@"\\.\DISPLAY12", "DISPLAY12")]
    [InlineData("DISPLAY3", "DISPLAY3")]
    [InlineData("monitor:0x1234", "monitor:0x1234")]
    public void FormatDisplayNameRemovesWindowsDevicePrefix(string deviceName, string expected)
    {
        Assert.Equal(expected, MonitorEnumerator.FormatDisplayName(deviceName));
    }
}

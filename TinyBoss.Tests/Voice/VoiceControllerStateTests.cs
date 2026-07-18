using TinyBoss.Voice;
using Xunit;

namespace TinyBoss.Tests.Voice;

public sealed class VoiceControllerStateTests
{
    [Fact]
    public void CanStartRecording_WhenIdle()
    {
        Assert.True(VoiceController.CanStartRecording(
            recording: false,
            stopInFlight: 0,
            injectInFlight: 0));
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 0, 1)]
    [InlineData(true, 1, 1)]
    public void CanStartRecording_BlocksBusyVoicePipeline(
        bool recording,
        int stopInFlight,
        int injectInFlight)
    {
        Assert.False(VoiceController.CanStartRecording(
            recording,
            stopInFlight,
            injectInFlight));
    }
}

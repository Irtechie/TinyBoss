using TinyBoss.Core;
using Xunit;

namespace TinyBoss.Tests.Core;

public sealed class ResumeCommandExtractorTests
{
    [Theory]
    [InlineData("copilot --resume=\"Configure OneDrive Output Path\"", "copilot --resume=\"Configure OneDrive Output Path\"")]
    [InlineData("copilot --resume=\"Evaluate Model Compatibility With Mcaps Copilot\"", "copilot --resume=\"Evaluate Model Compatibility With Mcaps Copilot\"")]
    [InlineData("codex resume 019dca28-149a-71a0-9c56-7350e30a9a55", "codex resume 019dca28-149a-71a0-9c56-7350e30a9a55")]
    [InlineData("inferno resume darkfactory-001", "inferno resume darkfactory-001")]
    [InlineData("> ghcp resume abc", "ghcp resume abc")]
    public void ExtractsKnownResumeCommands(string line, string expected)
    {
        Assert.Equal(expected, ResumeCommandExtractor.Extract(line));
    }

    [Fact]
    public void FindsLatestResumeCommandInTail()
    {
        var command = ResumeCommandExtractor.Find([
            "copilot --resume=\"Old\"",
            "some other text",
            "codex resume 019dd6c5-69fe-7b91-983e-7a9c9ad7f510"
        ]);

        Assert.Equal("codex resume 019dd6c5-69fe-7b91-983e-7a9c9ad7f510", command);
    }
}

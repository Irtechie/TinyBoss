using TinyBoss.Core;
using Xunit;

namespace TinyBoss.Tests.Core;

public sealed class ResumeCommandExtractorTests
{
    [Theory]
    [InlineData("copilot --resume=\"Configure OneDrive Output Path\"", "copilot --resume=\"Configure OneDrive Output Path\"")]
    [InlineData("copilot --resume=\"Evaluate Model Compatibility With Mcaps Copilot\"", "copilot --resume=\"Evaluate Model Compatibility With Mcaps Copilot\"")]
    [InlineData("codex resume 019dca28-149a-71a0-9c56-7350e30a9a55", "codex resume 019dca28-149a-71a0-9c56-7350e30a9a55")]
    [InlineData("codex resume 019de5db-e1ea-7643-83f3-fe91b0da3a79  ?", "codex resume 019de5db-e1ea-7643-83f3-fe91b0da3a79")]
    [InlineData("copilot --resume=\"Configure OneDrive Output Path\" mcaps 2", "copilot --resume=\"Configure OneDrive Output Path\"")]
    [InlineData("copilot --resume=\"Optimize Claude Permissions\" ATV Toolkit", "copilot --resume=\"Optimize Claude Permissions\"")]
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

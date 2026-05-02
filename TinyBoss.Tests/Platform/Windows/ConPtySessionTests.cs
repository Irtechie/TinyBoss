using System.Text;
using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class ConPtySessionTests
{
    [Fact]
    public async Task Start_OpensPseudoConsoleOutputStream()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        await using var session = ConPtySession.Start(cmd, null, null);
        Assert.Equal(Path.GetFileNameWithoutExtension(cmd), session.Process.ProcessName, ignoreCase: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = new StringBuilder();
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
        {
            output.Append(chunk);
            if (output.Length > 0)
                break;
        }

        Assert.NotEmpty(output.ToString());
    }
}

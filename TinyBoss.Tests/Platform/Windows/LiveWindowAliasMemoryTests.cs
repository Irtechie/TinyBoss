using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class LiveWindowAliasMemoryTests
{
    [Fact]
    public void RemembersAliasForLiveWindow()
    {
        var memory = CreateMemory();
        var hwnd = new nint(123);

        memory.Set(hwnd, "Build Agent");

        Assert.Equal("Build Agent", memory.Get(hwnd));
    }

    [Fact]
    public void TrimsAliasBeforeStoring()
    {
        var memory = CreateMemory();
        var hwnd = new nint(123);

        memory.Set(hwnd, "  Build Agent  ");

        Assert.Equal("Build Agent", memory.Get(hwnd));
    }

    [Fact]
    public void EmptyAliasClearsMemory()
    {
        var memory = CreateMemory();
        var hwnd = new nint(123);
        memory.Set(hwnd, "Build Agent");

        memory.Set(hwnd, "");

        Assert.Null(memory.Get(hwnd));
    }

    [Fact]
    public void PruneRemovesDeadWindows()
    {
        var memory = CreateMemory();
        var live = new nint(123);
        var dead = new nint(456);
        memory.Set(live, "Live");
        memory.Set(dead, "Dead");

        memory.Prune(hwnd => hwnd == live);

        Assert.Equal("Live", memory.Get(live));
        Assert.Null(memory.Get(dead));
    }

    private static LiveWindowAliasMemory CreateMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TinyBoss.Tests", Guid.NewGuid().ToString("N"));
        return new LiveWindowAliasMemory(Path.Combine(dir, "window-aliases.json"));
    }
}

using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class LiveWindowAliasMemoryTests
{
    [Fact]
    public void RemembersAliasForLiveWindow()
    {
        var memory = new LiveWindowAliasMemory();
        var hwnd = new nint(123);

        memory.Set(hwnd, "Build Agent");

        Assert.Equal("Build Agent", memory.Get(hwnd));
    }

    [Fact]
    public void TrimsAliasBeforeStoring()
    {
        var memory = new LiveWindowAliasMemory();
        var hwnd = new nint(123);

        memory.Set(hwnd, "  Build Agent  ");

        Assert.Equal("Build Agent", memory.Get(hwnd));
    }

    [Fact]
    public void EmptyAliasClearsMemory()
    {
        var memory = new LiveWindowAliasMemory();
        var hwnd = new nint(123);
        memory.Set(hwnd, "Build Agent");

        memory.Set(hwnd, "");

        Assert.Null(memory.Get(hwnd));
    }

    [Fact]
    public void PruneRemovesDeadWindows()
    {
        var memory = new LiveWindowAliasMemory();
        var live = new nint(123);
        var dead = new nint(456);
        memory.Set(live, "Live");
        memory.Set(dead, "Dead");

        memory.Prune(hwnd => hwnd == live);

        Assert.Equal("Live", memory.Get(live));
        Assert.Null(memory.Get(dead));
    }
}

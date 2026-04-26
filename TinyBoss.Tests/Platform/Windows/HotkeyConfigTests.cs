using TinyBoss;
using TinyBoss.Core;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class HotkeyConfigTests
{
    [Fact]
    public void MovePageDefaultDoesNotConflictWithVoiceOrTile()
    {
        var config = new TinyBossConfig();
        var voice = new HotkeyPreset("voice", config.VoiceModifiers, config.VoiceKey);
        var tile = new HotkeyPreset("tile", config.TileModifiers, config.TileKey);
        var movePage = new HotkeyPreset("move-page", config.MovePageModifiers, config.MovePageKey);

        Assert.False(SettingsWindow.HasHotkeyConflict(voice, tile, movePage));
    }

    [Fact]
    public void ConflictHelperRejectsDuplicateHotkeys()
    {
        var first = new HotkeyPreset("first", 0x0006, 0x47);
        var duplicate = new HotkeyPreset("duplicate", 0x0006, 0x47);
        var other = new HotkeyPreset("other", 0x0006, 0x4D);

        Assert.True(SettingsWindow.HasHotkeyConflict(first, duplicate, other));
    }
}

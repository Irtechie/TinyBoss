using TinyBoss.Platform.Windows;
using Xunit;

namespace TinyBoss.Tests.Platform.Windows;

public sealed class VoiceHotkeyStateTests
{
    private const int MOD_SHIFT = 0x0004;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_RMENU = 0xA5;
    private const int VK_OEM_3 = 0xC0;

    [Fact]
    public void ShiftBacktickStartsVoiceAndSuppressesBacktick()
    {
        var state = new VoiceHotkeyState();

        var shift = state.ProcessKeyEvent(VK_RSHIFT, isKeyDown: true, MOD_SHIFT, VK_OEM_3);
        var backtickDown = state.ProcessKeyEvent(VK_OEM_3, isKeyDown: true, MOD_SHIFT, VK_OEM_3);
        var backtickUp = state.ProcessKeyEvent(VK_OEM_3, isKeyDown: false, MOD_SHIFT, VK_OEM_3);
        var shiftUp = state.ProcessKeyEvent(VK_RSHIFT, isKeyDown: false, MOD_SHIFT, VK_OEM_3);

        Assert.False(shift.Suppress);
        Assert.False(shift.Started);
        Assert.True(backtickDown.Suppress);
        Assert.True(backtickDown.Started);
        Assert.True(backtickUp.Suppress);
        Assert.False(backtickUp.Stopped);
        Assert.True(shiftUp.Stopped);
    }

    [Fact]
    public void StandalonePrintableVoiceKeyDoesNotReachTarget()
    {
        var state = new VoiceHotkeyState();

        var down = state.ProcessKeyEvent(VK_OEM_3, isKeyDown: true, modifiers: 0, key: VK_OEM_3);
        var up = state.ProcessKeyEvent(VK_OEM_3, isKeyDown: false, modifiers: 0, key: VK_OEM_3);

        Assert.True(down.Suppress);
        Assert.True(down.Started);
        Assert.True(up.Suppress);
        Assert.True(up.Stopped);
    }

    [Fact]
    public void RightAltVoiceKeyIsSuppressed()
    {
        var state = new VoiceHotkeyState();

        var down = state.ProcessKeyEvent(VK_RMENU, isKeyDown: true, modifiers: 0, key: VK_RMENU);
        var up = state.ProcessKeyEvent(VK_RMENU, isKeyDown: false, modifiers: 0, key: VK_RMENU);

        Assert.True(down.Suppress);
        Assert.True(down.Started);
        Assert.True(up.Suppress);
        Assert.True(up.Stopped);
    }

    [Fact]
    public void RightAltVoiceKeySuppressesGenericAltAlias()
    {
        var state = new VoiceHotkeyState();

        var down = state.ProcessKeyEvent(VK_MENU, isKeyDown: true, modifiers: 0, key: VK_RMENU);
        var up = state.ProcessKeyEvent(VK_MENU, isKeyDown: false, modifiers: 0, key: VK_RMENU);

        Assert.True(down.Suppress);
        Assert.True(down.Started);
        Assert.True(up.Suppress);
        Assert.True(up.Stopped);
    }

    [Fact]
    public void ComboVoiceStaysActiveUntilWholeChordIsReleased()
    {
        var state = new VoiceHotkeyState();

        state.ProcessKeyEvent(VK_SHIFT, isKeyDown: true, MOD_SHIFT, VK_OEM_3);
        state.ProcessKeyEvent(VK_OEM_3, isKeyDown: true, MOD_SHIFT, VK_OEM_3);

        var shiftUp = state.ProcessKeyEvent(VK_SHIFT, isKeyDown: false, MOD_SHIFT, VK_OEM_3);
        var backtickUp = state.ProcessKeyEvent(VK_OEM_3, isKeyDown: false, MOD_SHIFT, VK_OEM_3);

        Assert.False(shiftUp.Suppress);
        Assert.False(shiftUp.Stopped);
        Assert.True(backtickUp.Suppress);
        Assert.True(backtickUp.Stopped);
    }
}

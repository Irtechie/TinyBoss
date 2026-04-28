namespace TinyBoss.Platform.Windows;

public sealed class VoiceHotkeyState
{
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private readonly HashSet<int> _downKeys = new();
    private readonly HashSet<int> _suppressedKeys = new();
    private bool _voiceActive;

    public VoiceHotkeyTransition ProcessKeyEvent(int vkCode, bool isKeyDown, int modifiers, int key)
    {
        var wasActive = _voiceActive;

        if (isKeyDown)
            _downKeys.Add(vkCode);
        else
            _downKeys.Remove(vkCode);

        var comboHeld = IsComboHeld(modifiers, key);
        var started = !wasActive && comboHeld;
        if (started)
            _voiceActive = true;

        var stopped = wasActive && !IsActivationHeld(modifiers, key);
        if (stopped)
            _voiceActive = false;

        var suppress = ShouldSuppress(vkCode, key, isKeyDown, wasActive, comboHeld);
        if (suppress && isKeyDown)
            _suppressedKeys.Add(vkCode);
        else if (!isKeyDown)
            _suppressedKeys.Remove(vkCode);

        return new VoiceHotkeyTransition(suppress, started, stopped);
    }

    public void Reset()
    {
        _downKeys.Clear();
        _suppressedKeys.Clear();
        _voiceActive = false;
    }

    private bool ShouldSuppress(int vkCode, int key, bool isKeyDown, bool wasActive, bool comboHeld)
    {
        if (_suppressedKeys.Contains(vkCode))
            return true;

        if (!IsConfiguredKey(vkCode, key))
            return false;

        return isKeyDown ? comboHeld : wasActive;
    }

    private bool IsComboHeld(int modifiers, int key)
    {
        if (!IsConfiguredKeyDown(key))
            return false;

        if ((modifiers & MOD_CONTROL) != 0 && !IsModifierDown(MOD_CONTROL))
            return false;
        if ((modifiers & MOD_SHIFT) != 0 && !IsModifierDown(MOD_SHIFT))
            return false;
        if ((modifiers & MOD_ALT) != 0 && !IsModifierDown(MOD_ALT))
            return false;
        if ((modifiers & MOD_WIN) != 0 && !IsModifierDown(MOD_WIN))
            return false;

        return true;
    }

    private bool IsActivationHeld(int modifiers, int key)
    {
        if (IsConfiguredKeyDown(key))
            return true;

        if ((modifiers & MOD_CONTROL) != 0 && IsModifierDown(MOD_CONTROL))
            return true;
        if ((modifiers & MOD_SHIFT) != 0 && IsModifierDown(MOD_SHIFT))
            return true;
        if ((modifiers & MOD_ALT) != 0 && IsModifierDown(MOD_ALT))
            return true;
        if ((modifiers & MOD_WIN) != 0 && IsModifierDown(MOD_WIN))
            return true;

        return false;
    }

    private bool IsConfiguredKeyDown(int key)
    {
        return key switch
        {
            VK_SHIFT or VK_LSHIFT or VK_RSHIFT => IsModifierDown(MOD_SHIFT),
            VK_CONTROL or VK_LCONTROL or VK_RCONTROL => IsModifierDown(MOD_CONTROL),
            VK_MENU or VK_LMENU or VK_RMENU => IsModifierDown(MOD_ALT),
            _ => _downKeys.Contains(key),
        };
    }

    private bool IsConfiguredKey(int vkCode, int key)
    {
        if (key == VK_SHIFT)
            return vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT;
        if (key == VK_CONTROL)
            return vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL;
        if (key == VK_MENU)
            return vkCode is VK_MENU or VK_LMENU or VK_RMENU;
        if (key == VK_LSHIFT)
            return vkCode is VK_SHIFT or VK_LSHIFT;
        if (key == VK_RSHIFT)
            return vkCode is VK_SHIFT or VK_RSHIFT;
        if (key == VK_LCONTROL)
            return vkCode is VK_CONTROL or VK_LCONTROL;
        if (key == VK_RCONTROL)
            return vkCode is VK_CONTROL or VK_RCONTROL;
        if (key == VK_LMENU)
            return vkCode is VK_MENU or VK_LMENU;
        if (key == VK_RMENU)
            return vkCode is VK_MENU or VK_RMENU;

        return vkCode == key;
    }

    private bool IsModifierDown(int modifier)
    {
        return modifier switch
        {
            MOD_CONTROL => _downKeys.Contains(VK_CONTROL) || _downKeys.Contains(VK_LCONTROL) || _downKeys.Contains(VK_RCONTROL),
            MOD_SHIFT => _downKeys.Contains(VK_SHIFT) || _downKeys.Contains(VK_LSHIFT) || _downKeys.Contains(VK_RSHIFT),
            MOD_ALT => _downKeys.Contains(VK_MENU) || _downKeys.Contains(VK_LMENU) || _downKeys.Contains(VK_RMENU),
            MOD_WIN => _downKeys.Contains(VK_LWIN) || _downKeys.Contains(VK_RWIN),
            _ => false,
        };
    }

}

public readonly record struct VoiceHotkeyTransition(bool Suppress, bool Started, bool Stopped);

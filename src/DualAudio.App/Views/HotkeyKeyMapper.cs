using System.Windows.Input;
using DualAudio.Core.Abstractions;

namespace DualAudio.App.Views;

/// <summary>Turns a WPF KeyDown in the settings window (recorder) into a hotkey. No hooks: only the focused window's own key events.</summary>
public static class HotkeyKeyMapper
{
    /// <summary>
    /// False while only modifiers are held (keep waiting). With Alt held WPF reports Key.System and the real key in
    /// SystemKey.
    /// </summary>
    public static bool TryMap(Key key, Key systemKey, ModifierKeys modifiers, out HotkeyModifiers hotkeyModifiers, out int virtualKey)
    {
        var actual = key == Key.System ? systemKey : key;
        hotkeyModifiers = HotkeyModifiers.None;
        virtualKey = 0;
        if (actual is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return false;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            hotkeyModifiers |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            hotkeyModifiers |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            hotkeyModifiers |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            hotkeyModifiers |= HotkeyModifiers.Windows;
        }

        virtualKey = KeyInterop.VirtualKeyFromKey(actual);
        return virtualKey != 0;
    }
}

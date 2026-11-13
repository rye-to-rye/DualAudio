using System.Diagnostics.CodeAnalysis;
using DualAudio.Core.Abstractions;

namespace DualAudio.Core.Hotkeys;

/// <summary>
/// Human-readable hotkey text ("Ctrl+Alt+PgUp") and back. Keys are Win32 virtual-key codes, kept as plain numbers so
/// Core stays Windows-free. Also decides which combinations are allowed as global shortcuts.
/// </summary>
public static class HotkeyFormat
{
    private static readonly (HotkeyModifiers Modifier, string Name)[] ModifierNames =
    [
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Windows, "Win"),
    ];

    private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();

    private static readonly Dictionary<string, int> KeysByName = BuildKeysByName();

    // Modifier keys themselves (Shift, Ctrl, Alt, Win, left/right variants) can't be the key of a shortcut.
    private static readonly HashSet<int> ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    /// <summary>"Ctrl+Alt+M". Unknown keys are shown as "Key 0x.."; never throws.</summary>
    public static string Format(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var parts = ModifierNames.Where(m => binding.Modifiers.HasFlag(m.Modifier)).Select(m => m.Name).ToList();
        parts.Add(KeyName(binding.VirtualKey));
        return string.Join("+", parts);
    }

    public static string KeyName(int virtualKey) =>
        KeyNames.TryGetValue(virtualKey, out var name) ? name : $"Key 0x{virtualKey:X2}";

    /// <summary>Parses "Ctrl+Alt+M" (case-insensitive, spaces allowed; "Control", "Windows", "PageUp" accepted too).</summary>
    public static bool TryParse(string? text, HotkeyAction action, [NotNullWhen(true)] out HotkeyBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!KeysByName.TryGetValue(parts[^1].ToUpperInvariant(), out var key))
        {
            return false;
        }

        binding = new HotkeyBinding(action, modifiers, key);
        return true;
    }

    /// <summary>
    /// Allowed as a global shortcut: at least Ctrl, Alt or Win (Shift alone would swallow typed characters in games
    /// and chat), and a real key that isn't itself a modifier.
    /// </summary>
    public static bool IsAllowed(HotkeyBinding binding) =>
        binding is not null
        && (binding.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0
        && binding.VirtualKey is > 0 and < 0xFF
        && !ModifierKeys.Contains(binding.VirtualKey);

    /// <summary>Same key combination (the action is ignored).</summary>
    public static bool SameCombination(HotkeyBinding a, HotkeyBinding b) =>
        a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey;

    private static Dictionary<int, string> BuildKeyNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x1B] = "Esc", [0x20] = "Space",
            [0x21] = "PgUp", [0x22] = "PgDn", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "PrtScn", [0x2D] = "Ins", [0x2E] = "Del",
            [0x6A] = "NumMultiply", [0x6B] = "NumAdd", [0x6D] = "NumSubtract", [0x6E] = "NumDecimal", [0x6F] = "NumDivide", // no "+" in names: it separates keys
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
            [0xAD] = "VolumeMute", [0xAE] = "VolumeDown", [0xAF] = "VolumeUp",
            [0xB0] = "MediaNext", [0xB1] = "MediaPrev", [0xB2] = "MediaStop", [0xB3] = "MediaPlay",
        };
        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = c.ToString();
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }

        for (var i = 0; i <= 9; i++)
        {
            names[0x60 + i] = $"Num{i}";
        }

        for (var i = 1; i <= 24; i++)
        {
            names[0x6F + i] = $"F{i}";
        }

        return names;
    }

    private static Dictionary<string, int> BuildKeysByName()
    {
        var byName = KeyNames.ToDictionary(p => p.Value.ToUpperInvariant(), p => p.Key);
        byName["PAGEUP"] = 0x21;
        byName["PAGEDOWN"] = 0x22;
        byName["ESCAPE"] = 0x1B;
        byName["INSERT"] = 0x2D;
        byName["DELETE"] = 0x2E;
        return byName;
    }
}

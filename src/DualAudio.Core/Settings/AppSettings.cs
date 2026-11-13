using DualAudio.Core.Abstractions;

namespace DualAudio.Core.Settings;

/// <summary>Persisted user settings. Bump <see cref="CurrentSchemaVersion"/> on breaking changes.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    // Win32 virtual-key codes.
    private const int VkM = 0x4D;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;

    public static IReadOnlyList<HotkeyBinding> DefaultHotkeys { get; } =
    [
        new(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt, VkM),
        new(HotkeyAction.VolumeUp, HotkeyModifiers.Control | HotkeyModifiers.Alt, VkPageUp),
        new(HotkeyAction.VolumeDown, HotkeyModifiers.Control | HotkeyModifiers.Alt, VkPageDown),
    ];

    public static AppSettings Default { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? PreferredDeviceId { get; init; }

    /// <summary>Device id -> user-chosen nickname.</summary>
    public IReadOnlyDictionary<string, string> DeviceNicknames { get; init; } = new Dictionary<string, string>();

    public bool AutoConnectOnStart { get; init; } = true;

    /// <summary>0..100, applied to every phone session.</summary>
    public int PhoneVolume { get; init; } = 100;

    public bool PhoneMuted { get; init; }

    public IReadOnlyList<HotkeyBinding> Hotkeys { get; init; } = DefaultHotkeys;

    public bool StartWithWindows { get; init; }

    /// <summary>
    /// Set when onboarding first succeeded (phone audio actually arrived). Afterwards onboarding, if it is shown
    /// again because no phone is saved, can be skipped.
    /// </summary>
    public bool OnboardingCompleted { get; init; }

    /// <summary>Set once the one-off "DualAudio is still running in the tray" notification has been shown.</summary>
    public bool TrayNoticeShown { get; init; }

    /// <summary>The binding for <paramref name="action"/>: the saved one, else the default; null if neither exists.</summary>
    public HotkeyBinding? HotkeyFor(HotkeyAction action) =>
        Hotkeys?.FirstOrDefault(h => h?.Action == action) ?? DefaultHotkeys.FirstOrDefault(h => h.Action == action);

    /// <summary>Replaces (or adds) the binding for <paramref name="binding"/>'s action; other bindings are kept.</summary>
    public AppSettings WithHotkey(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var others = (Hotkeys ?? DefaultHotkeys).Where(h => h is not null && h.Action != binding.Action);
        return this with { Hotkeys = [.. others, binding] };
    }

    /// <summary>Longest nickname kept; longer input is cut.</summary>
    public const int MaxNicknameLength = 40;

    /// <summary>The user's nickname for <paramref name="deviceId"/>, or null. DualAudio-only: never renames the system device.</summary>
    public string? NicknameFor(string? deviceId) =>
        deviceId is not null && DeviceNicknames.TryGetValue(deviceId, out var nickname) && !string.IsNullOrWhiteSpace(nickname)
            ? nickname
            : null;

    /// <summary>Sets (or, for a blank <paramref name="nickname"/>, removes) the nickname of one device.</summary>
    public AppSettings WithNickname(string deviceId, string? nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var nicknames = new Dictionary<string, string>(DeviceNicknames ?? new Dictionary<string, string>());
        var cleaned = CleanNickname(nickname);
        if (cleaned is null)
        {
            nicknames.Remove(deviceId);
        }
        else
        {
            nicknames[deviceId] = cleaned;
        }

        return this with { DeviceNicknames = nicknames };
    }

    /// <summary>"Forget in DualAudio": drops the device from DualAudio's settings only (it stays paired in Windows).</summary>
    public AppSettings Forget(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var forgotten = WithNickname(deviceId, null);
        return string.Equals(PreferredDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            ? forgotten with { PreferredDeviceId = null }
            : forgotten;
    }

    /// <summary>Trimmed, at most <see cref="MaxNicknameLength"/> characters; null when blank.</summary>
    public static string? CleanNickname(string? nickname)
    {
        var trimmed = nickname?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxNicknameLength ? trimmed : trimmed[..MaxNicknameLength].TrimEnd();
    }

    /// <summary>
    /// Repairs values a hand-edited or older file may contain: clamps volume, replaces null collections
    /// (deserializers can produce them despite the non-nullable types), drops null/duplicate hotkeys.
    /// </summary>
    public AppSettings Sanitize() => this with
    {
        PhoneVolume = Math.Clamp(PhoneVolume, 0, 100),
        DeviceNicknames = (DeviceNicknames ?? new Dictionary<string, string>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && CleanNickname(p.Value) is not null)
            .ToDictionary(p => p.Key, p => CleanNickname(p.Value)!),
        Hotkeys = Hotkeys is null
            ? DefaultHotkeys
            : Hotkeys.Where(h => h is not null).GroupBy(h => h.Action).Select(g => g.First()).ToList(),
        PreferredDeviceId = string.IsNullOrWhiteSpace(PreferredDeviceId) ? null : PreferredDeviceId,
    };
}

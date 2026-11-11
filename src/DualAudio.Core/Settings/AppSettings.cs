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
    /// Repairs values a hand-edited or older file may contain: clamps volume, replaces null collections
    /// (deserializers can produce them despite the non-nullable types), drops null/duplicate hotkeys.
    /// </summary>
    public AppSettings Sanitize() => this with
    {
        PhoneVolume = Math.Clamp(PhoneVolume, 0, 100),
        DeviceNicknames = DeviceNicknames ?? new Dictionary<string, string>(),
        Hotkeys = Hotkeys is null
            ? DefaultHotkeys
            : Hotkeys.Where(h => h is not null).GroupBy(h => h.Action).Select(g => g.First()).ToList(),
        PreferredDeviceId = string.IsNullOrWhiteSpace(PreferredDeviceId) ? null : PreferredDeviceId,
    };
}

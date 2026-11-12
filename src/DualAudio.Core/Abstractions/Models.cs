using DualAudio.Core.Errors;

namespace DualAudio.Core.Abstractions;

/// <summary>
/// A paired phone that can stream audio to this PC.
/// <para><see cref="IsConnected"/> comes from the watcher and is unreliable (Phase 0: it read "n/a"
/// after a link drop). Never use it as a connection signal; the coordinator state is authoritative.</para>
/// </summary>
public sealed record PhoneDevice(string Id, string Name, bool IsConnected);

/// <summary>Mirrors the state of the receive connection (AudioPlaybackConnectionState).</summary>
public enum ReceiverState
{
    Closed,
    Opened,
}

public enum RadioStatus
{
    /// <summary>Not determined yet (monitor starting) or reported as unknown.</summary>
    Unknown,
    On,

    /// <summary>Turned off by the user.</summary>
    Off,

    /// <summary>Disabled (e.g. airplane mode).</summary>
    Disabled,

    /// <summary>No Bluetooth radio exists on this PC.</summary>
    NotPresent,
}

/// <summary>
/// Result of opening a receive connection. <paramref name="Detail"/> is for logs and diagnostics
/// (may contain an HRESULT); it is never shown as a user-facing title.
/// </summary>
public sealed record OpenResult(bool Success, ErrorKind? ErrorKind, string? Detail)
{
    public static OpenResult Ok() => new(true, null, null);

    public static OpenResult Failed(ErrorKind kind, string? detail = null) => new(false, kind, detail);
}

/// <summary>Metadata of one WASAPI audio session on one render endpoint.</summary>
/// <param name="EndpointId">Render endpoint the session lives on.</param>
/// <param name="ProcessName">Process image name or path: "svchost", "svchost.exe" or "C:\Windows\System32\svchost.exe".</param>
/// <param name="ProcessId">Diagnostics only. Never used to identify the phone session (shared svchost, changes on reboot).</param>
/// <param name="DisplayName">Session display name, e.g. "Microphone (Shlawg A2DP SNK)".</param>
/// <param name="SessionIdentifier">IAudioSessionControl2::GetSessionIdentifier.</param>
/// <param name="SessionInstanceIdentifier">IAudioSessionControl2::GetSessionInstanceIdentifier.</param>
/// <param name="IsSystemSoundsSession">IAudioSessionControl2::IsSystemSoundsSession.</param>
public sealed record PhoneSessionInfo(
    string EndpointId,
    string ProcessName,
    int ProcessId,
    string? DisplayName,
    string? SessionIdentifier,
    string? SessionInstanceIdentifier,
    bool IsSystemSoundsSession);

/// <summary>Live state of one phone session (one per render endpoint).</summary>
/// <param name="EndpointId">Render endpoint the session lives on.</param>
/// <param name="IsOnDefaultEndpoint">True for the session on the default Multimedia endpoint (the one Windows plays to).</param>
/// <param name="IsActive">Session state is Active (Phase 0: only the default endpoint's session is Active while connected).</param>
/// <param name="VolumePercent">Session volume, 0..100.</param>
/// <param name="Muted">Session mute.</param>
public sealed record PhoneSessionState(string EndpointId, bool IsOnDefaultEndpoint, bool IsActive, int VolumePercent, bool Muted);

/// <summary>Snapshot of every phone session plus the default-endpoint session's peak level.</summary>
/// <param name="Sessions">All phone sessions found, on every active render endpoint.</param>
/// <param name="Peak">Peak level (0..1) of the phone session on the default endpoint; 0 when there is none.</param>
public sealed record PhoneAudioStatus(IReadOnlyList<PhoneSessionState> Sessions, float Peak)
{
    public static PhoneAudioStatus Unavailable { get; } = new([], 0f);

    /// <summary>False when no phone session exists anywhere; the UI explains why (not connected, nothing played yet).</summary>
    public bool Available => Sessions.Count > 0;

    public bool IsActiveOnDefault => Sessions.Any(s => s.IsOnDefaultEndpoint && s.IsActive);
}

/// <summary>A render (output) endpoint, e.g. the user's headset.</summary>
public sealed record AudioEndpoint(string Id, string Name, bool IsDefault);

public enum HotkeyAction
{
    ToggleConnection,
    ToggleMute,
    VolumeUp,
    VolumeDown,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>A global hotkey. <paramref name="VirtualKey"/> is a Win32 virtual-key code (a plain number keeps Core Windows-free).</summary>
public sealed record HotkeyBinding(HotkeyAction Action, HotkeyModifiers Modifiers, int VirtualKey);

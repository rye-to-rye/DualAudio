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

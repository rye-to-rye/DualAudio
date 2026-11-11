using DualAudio.Core.Settings;

namespace DualAudio.Core.Abstractions;

/// <summary>
/// Watches paired devices that can stream audio to this PC
/// (DeviceWatcher over AudioPlaybackConnection.GetDeviceSelector()). Events may arrive on any thread.
/// </summary>
public interface IBluetoothAudioSourceWatcher : IDisposable
{
    /// <summary>A device appeared. Its Name is the one to keep.</summary>
    event EventHandler<PhoneDeviceEventArgs>? DeviceAdded;

    /// <summary>Informational only: Phase 0 saw garbage here (Name="4", IsConnected=n/a).</summary>
    event EventHandler<PhoneDeviceEventArgs>? DeviceUpdated;

    event EventHandler<PhoneDeviceRemovedEventArgs>? DeviceRemoved;

    /// <summary>Initial enumeration finished: every currently paired device has been reported via DeviceAdded.</summary>
    event EventHandler? EnumerationCompleted;

    bool IsEnumerationCompleted { get; }

    void Start();

    void Stop();
}

/// <summary>
/// Owns at most one AudioPlaybackConnection; opening a new one replaces (disposes) the previous one.
/// Must not throw for expected failures: return <see cref="OpenResult.Failed"/> instead.
/// </summary>
public interface IAudioReceiver : IDisposable
{
    /// <summary>
    /// The current connection's StateChanged. "Connected" is decided by <see cref="ReceiverState.Opened"/>
    /// arriving here, not by reading <see cref="State"/> right after <see cref="OpenAsync"/>
    /// (it briefly reads Closed after a successful open).
    /// </summary>
    event EventHandler<ReceiverStateChangedEventArgs>? StateChanged;

    ReceiverState State { get; }

    /// <summary>TryCreateFromId + StartAsync + OpenAsync. Cancellation aborts the attempt.</summary>
    Task<OpenResult> OpenAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Disposes the current connection, if any. Safe to call when nothing is open.</summary>
    Task CloseAsync();
}

/// <summary>Reports the Bluetooth radio state. Read-only: DualAudio never toggles the radio.</summary>
public interface IRadioMonitor : IDisposable
{
    event EventHandler<RadioStatusChangedEventArgs>? StatusChanged;

    RadioStatus Status { get; }

    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Controls the phone's audio session(s). Windows keeps one phone session per render endpoint, so
/// volume/mute apply to ALL matching sessions (see <c>PhoneSessionMatcher</c>) and are re-applied when
/// a new matching session appears or the default endpoint changes.
/// </summary>
public interface IAudioSessionController : IDisposable
{
    /// <summary>A phone session appeared or disappeared on some endpoint.</summary>
    event EventHandler? PhoneSessionsChanged;

    Task<IReadOnlyList<PhoneSessionInfo>> GetPhoneSessionsAsync(CancellationToken cancellationToken);

    /// <param name="volumePercent">0..100.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SetPhoneVolumeAsync(int volumePercent, CancellationToken cancellationToken);

    Task SetPhoneMutedAsync(bool muted, CancellationToken cancellationToken);

    /// <summary>True when a phone session on the default endpoint is Active.</summary>
    Task<bool> IsReceivingAudioAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read-only view of render endpoints. Changing the default endpoint would need undocumented APIs and is
/// deliberately not offered; phone audio follows the Windows default output.
/// </summary>
public interface IOutputDeviceService : IDisposable
{
    event EventHandler<DefaultEndpointChangedEventArgs>? DefaultEndpointChanged;

    Task<IReadOnlyList<AudioEndpoint>> GetRenderEndpointsAsync(CancellationToken cancellationToken);

    Task<AudioEndpoint?> GetDefaultEndpointAsync(CancellationToken cancellationToken);
}

/// <summary>Global hotkeys via RegisterHotKey (no low-level keyboard hooks).</summary>
public interface IHotkeyService : IDisposable
{
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>Returns false if another app already owns the combination.</summary>
    bool TryRegister(HotkeyBinding binding);

    void Unregister(HotkeyAction action);

    void UnregisterAll();
}

public interface ISettingsStore
{
    /// <summary>Returns defaults when no settings exist or the file is unreadable.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>Time source, injected so timeouts and backoff are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

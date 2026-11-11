using DualAudio.Core.Abstractions;
using DualAudio.Core.Errors;

namespace DualAudio.Core.Connection;

/// <summary>States of <see cref="ConnectionCoordinator"/>. See docs/architecture.md for the diagram.</summary>
public enum ConnectionState
{
    /// <summary>Nothing requested yet (start-up), or a Bluetooth error cleared with nothing to resume.</summary>
    Idle,

    /// <summary>Checking the radio and whether the phone is known to the watcher. Transient.</summary>
    Enabling,

    /// <summary>The phone isn't in the watcher yet (start-up enumeration, or after Bluetooth came back on).</summary>
    WaitingForPhone,

    /// <summary>OpenAsync running, or it succeeded and the receiver's Opened event hasn't arrived yet.</summary>
    Opening,

    /// <summary>The receiver reported Opened: phone audio plays through the PC.</summary>
    Connected,

    /// <summary>
    /// No link. After a user Disconnect, ActiveDevice is null. After an unexpected link loss, ActiveDevice
    /// is the phone and a reconnect may be pending (<see cref="ConnectionCoordinator.IsReconnecting"/>).
    /// </summary>
    Disconnected,

    /// <summary>Failed; see <see cref="ConnectionCoordinator.LastError"/>.</summary>
    Error,
}

public sealed class ConnectionStateChangedEventArgs(
    ConnectionState previous,
    ConnectionState current,
    ErrorKind? error,
    PhoneDevice? device) : EventArgs
{
    public ConnectionState Previous { get; } = previous;

    public ConnectionState Current { get; } = current;

    /// <summary>Set when <see cref="Current"/> is Error, or Disconnected after a failed reconnect attempt.</summary>
    public ErrorKind? Error { get; } = error;

    public PhoneDevice? Device { get; } = device;
}

public sealed record ConnectionCoordinatorOptions
{
    /// <summary>Upper bound for IAudioReceiver.OpenAsync (Phase 0: a reconnect open took ~4 s).</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>After OpenAsync succeeds, how long to wait for StateChanged -> Opened (Phase 0: ~1 ms).</summary>
    public TimeSpan OpenedConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace period after an unexpected Closed/Removed before declaring the link lost. PC Bluetooth off
    /// produces Closed + radio Off + Removed within ~140 ms; this window collapses them into one Error(BluetoothOff).
    /// </summary>
    public TimeSpan LinkLossSettleWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Initial value of <see cref="ConnectionCoordinator.AutoReconnect"/>.</summary>
    public bool AutoReconnect { get; init; } = true;

    public ReconnectOptions Reconnect { get; init; } = new();
}

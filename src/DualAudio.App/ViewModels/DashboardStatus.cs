using DualAudio.Core.Connection;
using DualAudio.Core.Errors;

namespace DualAudio.App.ViewModels;

/// <summary>Colour of the status pill.</summary>
public enum StatusKind
{
    Disconnected,
    Connecting,
    Connected,
    Receiving,
    Error,
}

/// <summary>Status pill: colour + text. Pure, so every state is unit-tested.</summary>
public sealed record DashboardStatus(StatusKind Kind, string Text)
{
    public static DashboardStatus From(ConnectionState state, bool isReconnecting, bool isReceivingAudio, bool muted, bool gaveUp = false)
    {
        var mutedSuffix = muted ? " · Muted" : string.Empty;
        return state switch
        {
            // Calm, not red: DualAudio stopped looking on its own; the user can Retry.
            ConnectionState.Error when gaveUp => new(StatusKind.Disconnected, "Phone not found"),
            ConnectionState.Idle => new(StatusKind.Disconnected, "Disconnected"),
            ConnectionState.Disconnected when isReconnecting => new(StatusKind.Connecting, "Reconnecting…"),
            ConnectionState.Disconnected => new(StatusKind.Disconnected, "Disconnected"),
            ConnectionState.Enabling or ConnectionState.Opening => new(StatusKind.Connecting, "Connecting…"),
            ConnectionState.WaitingForPhone => new(StatusKind.Connecting, "Waiting for phone…"),
            ConnectionState.Connected when isReceivingAudio => new(StatusKind.Receiving, "Receiving audio" + mutedSuffix),
            ConnectionState.Connected => new(StatusKind.Connected, "Connected" + mutedSuffix),
            ConnectionState.Error => new(StatusKind.Error, "Error"),
            _ => new(StatusKind.Disconnected, "Disconnected"),
        };
    }
}

/// <summary>What the error banner's button does.</summary>
public enum ErrorAction
{
    None,
    OpenBluetoothSettings,
    Retry,
}

/// <summary>Error banner content: title + action sentence from <see cref="ErrorMessages"/>, plus a button.</summary>
public sealed record ErrorBanner(ErrorKind Kind, string Title, string Message, ErrorAction Action, bool IsRetryingAutomatically)
{
    public string? ActionLabel => Action switch
    {
        ErrorAction.OpenBluetoothSettings => "Open Bluetooth settings",
        ErrorAction.Retry => IsRetryingAutomatically ? "Retry now" : "Retry",
        _ => null,
    };

    /// <summary>
    /// Shown in Error, and in Disconnected after a failed reconnect attempt (with "retrying" wording). Not shown when
    /// the coordinator gave up on its own (<paramref name="gaveUp"/>): that is the calm "Phone not found" state.
    /// </summary>
    public static ErrorBanner? From(ConnectionState state, ErrorKind? error, bool isReconnecting, bool gaveUp = false)
    {
        if (gaveUp || error is not { } kind || state is not (ConnectionState.Error or ConnectionState.Disconnected))
        {
            return null;
        }

        var text = ErrorMessages.For(kind);
        var retrying = state == ConnectionState.Disconnected && isReconnecting;
        var message = retrying ? text.Action + " DualAudio is retrying automatically." : text.Action;
        return new ErrorBanner(kind, text.Title, message, ActionFor(kind), retrying);
    }

    public static ErrorAction ActionFor(ErrorKind kind) => kind switch
    {
        ErrorKind.BluetoothOff or ErrorKind.NoAdapter or ErrorKind.DeviceNotPaired => ErrorAction.OpenBluetoothSettings,
        ErrorKind.SinkNotSupported => ErrorAction.None,
        _ => ErrorAction.Retry,
    };
}

/// <summary>A phone in the device list.</summary>
/// <param name="Name">The user's nickname if set (DualAudio-only), otherwise the name Windows reports.</param>
/// <param name="IsSaved">The phone DualAudio remembers (connects to at start-up); listed first.</param>
public sealed record DeviceItem(string Id, string Name, bool IsSaved = false)
{
    public string DisplayName => IsSaved ? Name + " (saved)" : Name;

    public static DeviceItem From(Core.Abstractions.PhoneDevice device, string? nickname = null, bool isSaved = false) =>
        new(device.Id, nickname ?? (string.IsNullOrWhiteSpace(device.Name) ? "Unnamed phone" : device.Name), isSaved);

    public override string ToString() => DisplayName;
}

/// <summary>The calm "Phone not found" panel (coordinator gave up on its own; not an error).</summary>
public static class PhoneNotFound
{
    public const string Title = "Phone not found";

    public static string Message(string? phoneName) =>
        $"DualAudio stopped looking for {(string.IsNullOrWhiteSpace(phoneName) ? "your phone" : phoneName)}. " +
        "Turn on its Bluetooth and keep it near the PC, then press Retry.";
}

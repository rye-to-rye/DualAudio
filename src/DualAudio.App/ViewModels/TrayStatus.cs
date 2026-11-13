using DualAudio.Core.Connection;
using DualAudio.Core.Errors;

namespace DualAudio.App.ViewModels;

/// <summary>
/// Tray tooltip text. Pure, so every state is unit-tested. This tooltip (and the dashboard behind it) is the only
/// feedback for hotkeys: no window may pop over a game.
/// </summary>
public static class TrayStatus
{
    public const string Prefix = "DualAudio — ";

    /// <summary>Windows cuts tray tooltips at 127 characters.</summary>
    public const int MaxLength = 127;

    public static string ToolTip(
        ConnectionState state,
        bool isReconnecting,
        bool gaveUp,
        ErrorKind? error,
        bool isReceivingAudio,
        bool muted,
        string? phoneName,
        int volumePercent)
    {
        var name = string.IsNullOrWhiteSpace(phoneName) ? "your phone" : phoneName;
        var line = state switch
        {
            ConnectionState.Connected when muted => "Phone muted",
            ConnectionState.Connected when isReceivingAudio => "Receiving audio",
            ConnectionState.Connected => $"Connected to {name}",
            ConnectionState.Disconnected when isReconnecting => "Reconnecting…",
            ConnectionState.Enabling or ConnectionState.Opening => "Connecting…",
            ConnectionState.WaitingForPhone => "Waiting for phone…",
            ConnectionState.Error when gaveUp => PhoneNotFound.Title,
            ConnectionState.Error when error is { } kind => ErrorMessages.For(kind).Title,
            _ => "Disconnected",
        };

        var text = Prefix + line;
        if (state == ConnectionState.Connected)
        {
            text += $"\nPhone volume {Math.Clamp(volumePercent, 0, 100)}%";
        }

        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}

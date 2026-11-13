namespace DualAudio.Core.Errors;

/// <summary>A short title plus one actionable sentence, written for non-technical users.</summary>
public sealed record ErrorMessage(string Title, string Action);

public static class ErrorMessages
{
    public static ErrorMessage For(ErrorKind kind) => kind switch
    {
        ErrorKind.BluetoothOff => new(
            "Bluetooth is off",
            "Turn on Bluetooth in Quick Settings or Settings > Bluetooth & devices, and DualAudio will reconnect."),
        ErrorKind.NoAdapter => new(
            "No Bluetooth adapter found",
            "Plug in or enable a Bluetooth adapter, and DualAudio will reconnect."),
        ErrorKind.SinkNotSupported => new(
            "This PC can't receive phone audio",
            "Update your Bluetooth driver from the PC or adapter maker's website, or use a different Bluetooth adapter."),
        ErrorKind.DeviceNotPaired => new(
            "Phone isn't paired",
            "Pair your phone in Settings > Bluetooth & devices, then choose it in DualAudio."),
        ErrorKind.DeviceNotFound => new(
            "Phone not reachable",
            "Make sure your phone's Bluetooth is on and the phone is near the PC, then try again."),
        ErrorKind.OpenTimedOut => new(
            "Phone didn't respond",
            "Unlock your phone, keep it close to the PC, and try connecting again."),
        ErrorKind.DeniedBySystem => new(
            "Windows blocked the connection",
            "Close other apps that use Bluetooth audio, then try again."),
        ErrorKind.PhoneUnreachable => new(
            "Can't reach your phone",
            "Is Bluetooth on and is it nearby? Turn on your phone's Bluetooth and keep it close to the PC, then try again."),
        ErrorKind.Unknown => new(
            "Couldn't connect to your phone",
            "Try again; if it keeps happening, turn Bluetooth off and on again."),
        _ => new(
            "Something went wrong",
            "Try again; if it keeps happening, restart DualAudio."),
    };
}

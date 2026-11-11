namespace DualAudio.Core.Abstractions;

public sealed class PhoneDeviceEventArgs(PhoneDevice device) : EventArgs
{
    public PhoneDevice Device { get; } = device;
}

public sealed class PhoneDeviceRemovedEventArgs(string deviceId) : EventArgs
{
    public string DeviceId { get; } = deviceId;
}

public sealed class ReceiverStateChangedEventArgs(string deviceId, ReceiverState state) : EventArgs
{
    public string DeviceId { get; } = deviceId;

    public ReceiverState State { get; } = state;
}

public sealed class RadioStatusChangedEventArgs(RadioStatus status) : EventArgs
{
    public RadioStatus Status { get; } = status;
}

public sealed class HotkeyPressedEventArgs(HotkeyAction action) : EventArgs
{
    public HotkeyAction Action { get; } = action;
}

public sealed class DefaultEndpointChangedEventArgs(AudioEndpoint? endpoint) : EventArgs
{
    public AudioEndpoint? Endpoint { get; } = endpoint;
}

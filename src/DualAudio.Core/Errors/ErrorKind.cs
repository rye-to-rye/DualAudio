namespace DualAudio.Core.Errors;

/// <summary>Every failure the UI can show. Platform adapters map HRESULTs/WinRT statuses to these.</summary>
public enum ErrorKind
{
    BluetoothOff,
    NoAdapter,
    SinkNotSupported,
    DeviceNotPaired,
    DeviceNotFound,
    OpenTimedOut,
    DeniedBySystem,
    Unknown,
}

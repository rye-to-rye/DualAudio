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

    /// <summary>
    /// OpenAsync failed with UnknownFailure 0x8007001F (ERROR_GEN_FAILURE). On hardware this is what a phone with
    /// Bluetooth off, out of range, or not ready yet produces (BUG-003). Retryable, like <see cref="Unknown"/>.
    /// </summary>
    PhoneUnreachable,
    Unknown,
}

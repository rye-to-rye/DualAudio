using DualAudio.Core.Abstractions;
using DualAudio.Core.Errors;
using DualAudio.Platform.Windows.Internal;
using Windows.Media.Audio;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>Maps AudioPlaybackConnection outcomes to Core <see cref="ErrorKind"/> values. Pure, so it is unit-tested.</summary>
internal static class OpenResultMapping
{
    // HRESULT_FROM_WIN32 values that WinRT Bluetooth calls are known to surface.
    internal const int ElementNotFound = unchecked((int)0x80070490);     // ERROR_NOT_FOUND
    internal const int DeviceNotConnected = unchecked((int)0x8007048F);  // ERROR_DEVICE_NOT_CONNECTED
    internal const int DeviceNotAvailable = unchecked((int)0x800710DF);  // ERROR_DEVICE_NOT_AVAILABLE
    internal const int AccessDenied = unchecked((int)0x80070005);        // E_ACCESSDENIED
    internal const int Timeout = unchecked((int)0x800705B4);             // ERROR_TIMEOUT
    internal const int SemaphoreTimeout = unchecked((int)0x80070079);    // ERROR_SEM_TIMEOUT
    internal const int NotSupported = unchecked((int)0x80070032);        // ERROR_NOT_SUPPORTED

    /// <summary>Maps the result of <c>AudioPlaybackConnection.OpenAsync</c>. The HRESULT goes into Detail only.</summary>
    public static OpenResult FromStatus(AudioPlaybackConnectionOpenResultStatus status, int extendedErrorHResult)
    {
        var detail = extendedErrorHResult == 0
            ? $"OpenAsync Status={status}"
            : $"OpenAsync Status={status} ExtendedError={SafeEvent.HResult(extendedErrorHResult)}";

        return status switch
        {
            AudioPlaybackConnectionOpenResultStatus.Success => OpenResult.Ok(),
            AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => OpenResult.Failed(ErrorKind.OpenTimedOut, detail),
            AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => OpenResult.Failed(ErrorKind.DeniedBySystem, detail),
            AudioPlaybackConnectionOpenResultStatus.UnknownFailure => OpenResult.Failed(ErrorKind.Unknown, detail),
            _ => OpenResult.Failed(ErrorKind.Unknown, detail),
        };
    }

    /// <summary>Maps an exception thrown by TryCreateFromId / StartAsync / OpenAsync. Never includes the message.</summary>
    public static OpenResult FromException(string step, Exception ex) =>
        OpenResult.Failed(KindForHResult(ex.HResult), $"{step} threw {ex.GetType().Name} HRESULT={SafeEvent.HResult(ex.HResult)}");

    public static ErrorKind KindForHResult(int hresult) => hresult switch
    {
        ElementNotFound or DeviceNotConnected or DeviceNotAvailable => ErrorKind.DeviceNotFound,
        AccessDenied => ErrorKind.DeniedBySystem,
        Timeout or SemaphoreTimeout => ErrorKind.OpenTimedOut,
        NotSupported => ErrorKind.SinkNotSupported,
        _ => ErrorKind.Unknown,
    };
}

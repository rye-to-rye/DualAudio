using System.Diagnostics;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.System;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>Opens Settings &gt; Bluetooth &amp; devices, where the user pairs the phone (never paired programmatically).</summary>
public sealed class BluetoothSettingsLauncher
{
    public static readonly Uri BluetoothSettingsUri = new("ms-settings:bluetooth");

    private readonly ILogger _logger;

    public BluetoothSettingsLauncher(ILogger<BluetoothSettingsLauncher>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Returns false if Settings could not be opened. Never throws (except for cancellation).</summary>
    public async Task<bool> OpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await Launcher.LaunchUriAsync(BluetoothSettingsUri).AsTask(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _logger.LogWarning("Launcher.LaunchUriAsync({Uri}) returned false; trying the shell", BluetoothSettingsUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Launcher.LaunchUriAsync threw HRESULT={HResult}; trying the shell", SafeEvent.HResult(ex.HResult));
        }

        // Fallback for unpackaged processes where the WinRT launcher is unavailable.
        try
        {
            using var process = Process.Start(new ProcessStartInfo(BluetoothSettingsUri.OriginalString) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Uri} failed", BluetoothSettingsUri);
            return false;
        }
    }
}

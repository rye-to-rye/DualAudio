using System.Diagnostics;
using DualAudio.Platform.Windows.Bluetooth;
using Microsoft.Extensions.Logging;

namespace DualAudio.App.Services;

/// <summary>Opens Windows Settings pages. Implementations never throw.</summary>
public interface ISystemSettingsLauncher
{
    /// <summary>ms-settings:bluetooth (pairing is always done there by the user).</summary>
    Task OpenBluetoothSettingsAsync();

    /// <summary>ms-settings:apps-volume (App volume and device preferences).</summary>
    Task OpenAppVolumeSettingsAsync();
}

public sealed class SystemSettingsLauncher(BluetoothSettingsLauncher bluetooth, ILogger<SystemSettingsLauncher> logger) : ISystemSettingsLauncher
{
    private const string AppVolumeUri = "ms-settings:apps-volume";

    public async Task OpenBluetoothSettingsAsync()
    {
        try
        {
            await bluetooth.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Opening Bluetooth settings failed");
        }
    }

    public Task OpenAppVolumeSettingsAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppVolumeUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Opening {Uri} failed", AppVolumeUri);
        }

        return Task.CompletedTask;
    }
}

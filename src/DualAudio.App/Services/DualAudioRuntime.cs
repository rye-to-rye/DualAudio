using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DualAudio.App.Services;

/// <summary>
/// Starts the platform sources after every subscriber (coordinator, phone audio, diagnostics, ViewModel) exists,
/// auto-connects the remembered phone, and on exit releases the connection before the process ends (BUG-001).
/// Sleep/resume, lock/unlock and logoff are handled by the coordinator (it subscribes to <see cref="ISystemPowerEvents"/>);
/// this class only starts that source.
/// </summary>
public sealed class DualAudioRuntime(
    ConnectionCoordinator coordinator,
    PhoneAudioService phoneAudio,
    IRadioMonitor radio,
    IBluetoothAudioSourceWatcher watcher,
    ISettingsStore settings,
    ILogger<DualAudioRuntime> logger,
    ISystemPowerEvents? power = null,
    ConnectionDiagnostics? diagnostics = null,
    ResourceMonitor? resources = null)
{
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Runs off the UI thread. Never throws (failures are logged and surface through coordinator state).</summary>
    public async Task StartAsync()
    {
        var token = _lifetime.Token;
        try
        {
            diagnostics?.Capture("startup");
            resources?.Start(); // one RESOURCES line now, then every 10 min
            await phoneAudio.InitializeAsync(token).ConfigureAwait(false);
            await radio.StartAsync(token).ConfigureAwait(false);
            logger.LogInformation("Bluetooth radio: {Status}", radio.Status);
            watcher.Start();
            power?.Start();

            var current = await settings.LoadAsync(token).ConfigureAwait(false);
            if (current.AutoConnectOnStart && current.PreferredDeviceId is { } deviceId)
            {
                // Waits for the watcher to report the phone, retries with ReconnectPolicy (e.g. phone Bluetooth off),
                // and ends in the calm "Phone not found" state (with slow background retries) if that runs out.
                logger.LogInformation("Auto-connecting to remembered phone {Device}", LogRedaction.DeviceId(deviceId));
                await coordinator.AutoConnectAsync(deviceId, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Exiting during start-up.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Start-up failed");
        }
    }

    /// <summary>Disposes phone audio and diagnostics, then the coordinator, which closes and disposes the connection (awaited).</summary>
    public async Task StopAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await phoneAudio.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disposing phone audio failed");
        }

        try
        {
            if (diagnostics is not null)
            {
                await diagnostics.DisposeAsync().ConfigureAwait(false);
            }

            if (resources is not null)
            {
                await resources.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing diagnostics failed");
        }

        try
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Connection coordinator disposed");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disposing the connection coordinator failed");
        }

        try
        {
            watcher.Stop();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stopping the watcher failed");
        }
    }
}

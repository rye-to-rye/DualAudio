using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Radios;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>
/// Radio.RequestAccessAsync + Radio.SetStateAsync(On) on the first Bluetooth radio. Only called after the user
/// clicked a button labelled "Turn on Bluetooth"; the result is reported back, never retried on its own.
/// </summary>
public sealed class BluetoothRadioControl : IBluetoothRadioControl
{
    private readonly ILogger _logger;

    public BluetoothRadioControl(ILogger<BluetoothRadioControl>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<RadioTurnOnResult> TurnOnAsync(CancellationToken cancellationToken)
    {
        try
        {
            var access = await Radio.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (access != RadioAccessStatus.Allowed)
            {
                _logger.LogWarning("Radio access not allowed ({Access}); the user must turn Bluetooth on in Settings", access);
                return RadioTurnOnResult.AccessDenied;
            }

            var radios = await Radio.GetRadiosAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bluetooth is null)
            {
                return RadioTurnOnResult.NoRadio;
            }

            if (bluetooth.State == RadioState.On)
            {
                return RadioTurnOnResult.AlreadyOn;
            }

            _logger.LogInformation("User asked to turn Bluetooth on (radio was {State})", bluetooth.State);
            var result = await bluetooth.SetStateAsync(RadioState.On).AsTask(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Radio.SetStateAsync(On) -> {Result}", result);
            return result switch
            {
                RadioAccessStatus.Allowed => RadioTurnOnResult.TurnedOn,
                RadioAccessStatus.DeniedByUser or RadioAccessStatus.DeniedBySystem => RadioTurnOnResult.AccessDenied,
                _ => RadioTurnOnResult.Failed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Turning Bluetooth on failed (HRESULT={HResult})", SafeEvent.HResult(ex.HResult));
            return RadioTurnOnResult.Failed;
        }
    }
}

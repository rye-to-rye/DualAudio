using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// OPTIONAL — MASTER volume of the current default output (AudioEndpointVolume). This changes EVERYTHING on that
/// device (game audio too), not just the phone; use <see cref="NAudioSessionController"/> for phone-only volume.
/// Not part of any Core interface and not used by the phone volume/mute features.
/// Every call opens the current default endpoint, so it always targets whatever Windows plays to right now.
/// </summary>
public sealed class DefaultEndpointMasterVolume : IDisposable
{
    private readonly ILogger _logger;
    private readonly MtaWorker _worker;

    public DefaultEndpointMasterVolume(ILogger<DefaultEndpointMasterVolume>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _worker = new MtaWorker("DualAudio master volume", _logger);
    }

    /// <summary>Master volume 0..100 and mute of the default endpoint, or null when there is none / it failed.</summary>
    public Task<(int VolumePercent, bool Muted)?> GetAsync(CancellationToken cancellationToken) =>
        RunAsync<(int, bool)?>(v => ((int)Math.Round(v.MasterVolumeLevelScalar * 100f), v.Mute), cancellationToken);

    /// <summary>Sets the master volume (0..100, clamped). Returns false when there is no default endpoint / it failed.</summary>
    public async Task<bool> SetVolumeAsync(int volumePercent, CancellationToken cancellationToken) =>
        await RunAsync<bool?>(v =>
        {
            v.MasterVolumeLevelScalar = Math.Clamp(volumePercent, 0, 100) / 100f;
            return true;
        }, cancellationToken).ConfigureAwait(false) ?? false;

    public async Task<bool> SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
        await RunAsync<bool?>(v =>
        {
            v.Mute = muted;
            return true;
        }, cancellationToken).ConfigureAwait(false) ?? false;

    public void Dispose() => _worker.Dispose();

    private async Task<T?> RunAsync<T>(Func<AudioEndpointVolume, T?> work, CancellationToken cancellationToken)
    {
        try
        {
            return await _worker.InvokeAsync(
                () =>
                {
                    using var enumerator = new MMDeviceEnumerator();
                    if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null)
                    {
                        return default;
                    }

                    using (device)
                    {
                        return work(device.AudioEndpointVolume); // disposed by the MMDevice
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Master volume operation failed");
            return default;
        }
    }
}

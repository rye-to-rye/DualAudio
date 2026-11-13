using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Foundation.Metadata;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>
/// Onboarding checks: OS build, Bluetooth radio present and On (from <see cref="IRadioMonitor"/>), and the number of
/// paired A2DP sources the running <see cref="A2dpSourceWatcher"/> reports. Read-only; never changes anything.
/// The composition root must have started the radio monitor and the watcher (DualAudioRuntime) before checking.
/// </summary>
public sealed class CapabilityService : ICapabilityService
{
    private const string PlaybackConnectionType = "Windows.Media.Audio.AudioPlaybackConnection";
    private static readonly TimeSpan EnumerationWait = TimeSpan.FromSeconds(10);

    private readonly IRadioMonitor _radio;
    private readonly A2dpSourceWatcher _watcher;
    private readonly ILogger _logger;

    public CapabilityService(IRadioMonitor radio, A2dpSourceWatcher watcher, ILogger<CapabilityService>? logger = null)
    {
        _radio = radio;
        _watcher = watcher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<CapabilityReport> CheckAsync(CancellationToken cancellationToken)
    {
        var build = Environment.OSVersion.Version.Build;
        var osOk = OperatingSystem.IsWindowsVersionAtLeast(10, 0, CapabilityReport.MinimumOsBuild) && IsPlaybackConnectionPresent();

        var radio = RadioStatus.Unknown;
        try
        {
            await _radio.StartAsync(cancellationToken).ConfigureAwait(false); // idempotent, bounded
            radio = _radio.Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the Bluetooth radio for the capability check failed");
        }

        var complete = await WaitForEnumerationAsync(cancellationToken).ConfigureAwait(false);
        var sources = _watcher.Devices.Count;

        var report = new CapabilityReport(build, osOk, radio, sources, complete);
        _logger.LogInformation(
            "Capability check: build {Build} (supported {Supported}), radio {Radio}, A2DP sources {Count} (list complete {Complete})",
            build,
            osOk,
            radio,
            sources,
            complete);
        return report;
    }

    /// <summary>
    /// The NetBIOS computer name (<see cref="Environment.MachineName"/>), which is what Windows advertises over Bluetooth:
    /// hardware showed phones listing "RYELAPTOP", while the DNS host name read "RyeLaptop" and the adapter's
    /// DeviceInformation.Name read "RYELAPTOP- front".
    /// </summary>
    public Task<string> GetPcBluetoothNameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Environment.MachineName;
        _logger.LogInformation("PC Bluetooth name: \"{Name}\"", name);
        return Task.FromResult(name);
    }

    private static bool IsPlaybackConnectionPresent()
    {
        try
        {
            return ApiInformation.IsTypePresent(PlaybackConnectionType);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> WaitForEnumerationAsync(CancellationToken cancellationToken)
    {
        if (_watcher.IsEnumerationCompleted)
        {
            return true;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, EventArgs e) => completed.TrySetResult();
        _watcher.EnumerationCompleted += OnCompleted;
        try
        {
            if (_watcher.IsEnumerationCompleted)
            {
                return true;
            }

            await completed.Task.WaitAsync(EnumerationWait, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("A2DP source enumeration did not complete within {Timeout}", EnumerationWait);
            return false;
        }
        finally
        {
            _watcher.EnumerationCompleted -= OnCompleted;
        }
    }
}

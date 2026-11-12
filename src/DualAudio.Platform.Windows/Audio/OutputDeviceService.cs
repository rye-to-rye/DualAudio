using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// Read-only view of render endpoints (MMDeviceEnumerator) with default-endpoint (Role.Multimedia) tracking via
/// IMMNotificationClient (NAudio's <see cref="MMDeviceNotificationClient"/> wrapper). Registration happens on
/// the first call or <see cref="StartAsync"/>; <see cref="Dispose"/> unregisters. All COM calls run on one MTA
/// thread; events are raised on a background thread.
/// </summary>
public sealed class OutputDeviceService : IOutputDeviceService
{
    private readonly ILogger _logger;
    private readonly MtaWorker _worker;
    private int _disposed;

    // Worker-thread only.
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private string? _lastDefaultId;

    public OutputDeviceService(ILogger<OutputDeviceService>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _worker = new MtaWorker("DualAudio output devices", _logger);
    }

    public event EventHandler<DefaultEndpointChangedEventArgs>? DefaultEndpointChanged;

    public event EventHandler? DevicesChanged;

    /// <summary>Registers for notifications now, so DefaultEndpointChanged fires before anything is queried.</summary>
    public Task StartAsync(CancellationToken cancellationToken) =>
        RunAsync(() => true, false, cancellationToken);

    public Task<IReadOnlyList<AudioEndpoint>> GetRenderEndpointsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<AudioEndpoint>>(ReadEndpoints, [], cancellationToken);

    public Task<AudioEndpoint?> GetDefaultEndpointAsync(CancellationToken cancellationToken) =>
        RunAsync(ReadDefault, null, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _worker.InvokeAsync(() =>
            {
                _notifications?.Dispose(); // unregisters the IMMNotificationClient
                _notifications = null;
                _enumerator?.Dispose();
                _enumerator = null;
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unregistering endpoint notifications failed");
        }

        _worker.Dispose();
    }

    private async Task<T> RunAsync<T>(Func<T> work, T fallback, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return fallback;
        }

        try
        {
            return await _worker.InvokeAsync(
                () =>
                {
                    EnsureStarted();
                    return work();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading audio endpoints failed");
            return fallback;
        }
    }

    private void EnsureStarted()
    {
        if (_enumerator is not null)
        {
            return;
        }

        var enumerator = new MMDeviceEnumerator();
        try
        {
            var notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);

            // Audio-stack thread: only post; the worker does the COM reads.
            notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
                {
                    _worker.Post(OnDefaultChanged);
                }
            };
            notifications.DeviceAdded += (_, _) => _worker.Post(OnDevicesChanged);
            notifications.DeviceRemoved += (_, _) => _worker.Post(OnDevicesChanged);
            notifications.DeviceStateChanged += (_, _) => _worker.Post(OnDevicesChanged);
            _notifications = notifications;
            _enumerator = enumerator;
            _lastDefaultId = ReadDefault()?.Id;
        }
        catch
        {
            _notifications?.Dispose();
            _notifications = null;
            enumerator.Dispose();
            _enumerator = null;
            throw;
        }
    }

    private void OnDefaultChanged()
    {
        if (Volatile.Read(ref _disposed) == 1 || _enumerator is null)
        {
            return;
        }

        AudioEndpoint? endpoint;
        try
        {
            endpoint = ReadDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the new default endpoint failed");
            endpoint = null;
        }

        if (string.Equals(endpoint?.Id, _lastDefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return; // Windows sends one notification per role; we only care about Multimedia, once.
        }

        _lastDefaultId = endpoint?.Id;
        _logger.LogInformation("Default output is now \"{Name}\"", endpoint?.Name ?? "(none)");
        var handler = DefaultEndpointChanged;
        _ = Task.Run(() => SafeEvent.Raise(handler, this, new DefaultEndpointChangedEventArgs(endpoint), _logger, nameof(DefaultEndpointChanged)));
    }

    private void OnDevicesChanged()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        var handler = DevicesChanged;
        _ = Task.Run(() => SafeEvent.Raise(handler, this, _logger, nameof(DevicesChanged)));
    }

    private AudioEndpoint? ReadDefault()
    {
        if (!_enumerator!.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return new AudioEndpoint(device.ID, FriendlyName(device), IsDefault: true);
        }
    }

    private List<AudioEndpoint> ReadEndpoints()
    {
        var defaultId = ReadDefault()?.Id;
        var list = new List<AudioEndpoint>();
        using var devices = _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            var id = device.ID;
            list.Add(new AudioEndpoint(id, FriendlyName(device), string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }

    private string FriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading an endpoint name failed");
            return "(unknown output)";
        }
    }
}

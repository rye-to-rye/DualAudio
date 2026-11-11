using DualAudio.Core.Abstractions;
using DualAudio.Core.Diagnostics;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Audio;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>
/// Paired devices that can stream audio to this PC: a DeviceWatcher over
/// <see cref="AudioPlaybackConnection.GetDeviceSelector"/>.
/// <para>Events are raised on the WinRT callback thread (no SynchronizationContext is captured).
/// If the watcher stops with <see cref="DeviceWatcherStatus.Aborted"/> it is recreated with backoff; devices
/// that are not re-reported by the new watcher's enumeration are then raised as removed.</para>
/// </summary>
public sealed class A2dpSourceWatcher : IBluetoothAudioSourceWatcher
{
    internal const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly object _sync = new();

    // All fields below are guarded by _sync. Events are raised only after _sync is released.
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingAdd> _pending = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _unconfirmed;
    private Session? _session;
    private CancellationTokenSource? _restartCts;
    private int _restartFailures;
    private bool _enumerationCompleted;
    private bool _running;
    private bool _disposed;

    public A2dpSourceWatcher(ILogger<A2dpSourceWatcher>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<PhoneDeviceEventArgs>? DeviceAdded;

    public event EventHandler<PhoneDeviceEventArgs>? DeviceUpdated;

    public event EventHandler<PhoneDeviceRemovedEventArgs>? DeviceRemoved;

    public event EventHandler? EnumerationCompleted;

    public bool IsEnumerationCompleted
    {
        get
        {
            lock (_sync)
            {
                return _enumerationCompleted;
            }
        }
    }

    /// <summary>Current devices, in no particular order. Names are the ones reported by Added.</summary>
    public IReadOnlyList<PhoneDevice> Devices
    {
        get
        {
            lock (_sync)
            {
                return _devices.Values.Select(e => e.Device).ToList();
            }
        }
    }

    /// <summary>Starts watching. Idempotent. Never throws for WinRT failures: they are logged and retried.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _restartFailures = 0;
            StartSession_NoLock("start");
        }
    }

    /// <summary>Stops watching. Idempotent. The device list is kept and reconciled on the next Start.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            CancelRestart_NoLock();
            StopSession_NoLock();
            _logger.LogInformation("A2DP source watcher stopped");
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private static PhoneDevice ToPhoneDevice(DeviceInformation info, string? name = null)
    {
        var isConnected = info.Properties.TryGetValue(IsConnectedKey, out var value) && value is bool b && b;
        return new PhoneDevice(info.Id, name ?? info.Name ?? string.Empty, isConnected);
    }

    // ---------------------------------------------------------------- session lifecycle

    private void StartSession_NoLock(string why)
    {
        Session? session = null;
        try
        {
            var selector = AudioPlaybackConnection.GetDeviceSelector();
            DeviceWatcher watcher;
            try
            {
                watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey, .. BluetoothDeviceNameResolver.InterfaceProperties]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CreateWatcher with extra properties failed; retrying without them");
                watcher = DeviceInformation.CreateWatcher(selector);
            }

            session = new Session(this, watcher);
            session.Attach();

            // Devices known from before a restart must be re-reported by this enumeration, or they are removed.
            _unconfirmed = _devices.Count > 0 ? new HashSet<string>(_devices.Keys, StringComparer.OrdinalIgnoreCase) : null;
            _session = session;
            watcher.Start();
            _logger.LogInformation("A2DP source watcher started ({Why})", why);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the A2DP source watcher failed ({Why})", why);
            if (session is not null)
            {
                session.Detach(_logger);
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
            }

            ScheduleRestart_NoLock();
        }
    }

    private void StopSession_NoLock()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        // Unsubscribe first so nothing is reported after Stop returns; abandon pending name lookups.
        session.Detach(_logger);
        _pending.Clear();
        try
        {
            if (session.Watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                session.Watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeviceWatcher.Stop failed");
        }
    }

    private void ScheduleRestart_NoLock()
    {
        if (!_running || _disposed)
        {
            return;
        }

        CancelRestart_NoLock();
        var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, _restartFailures), MaxRestartDelay.TotalSeconds));
        _restartFailures++;
        var cts = new CancellationTokenSource();
        _restartCts = cts;
        _logger.LogWarning("Restarting the A2DP source watcher in {Delay}", delay);
        _ = RestartLaterAsync(delay, cts.Token);
    }

    private void CancelRestart_NoLock()
    {
        if (_restartCts is null)
        {
            return;
        }

        _restartCts.Cancel();
        _restartCts.Dispose();
        _restartCts = null;
    }

    private async Task RestartLaterAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            lock (_sync)
            {
                if (token.IsCancellationRequested || !_running || _disposed)
                {
                    return;
                }

                StartSession_NoLock("restart after abort");
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or disposed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2DP source watcher restart failed");
        }
    }

    // ---------------------------------------------------------------- WinRT handlers

    private void OnAdded(Session session, DeviceInformation info)
    {
        try
        {
            PendingAdd pending;
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                _unconfirmed?.Remove(info.Id);
                if (_devices.ContainsKey(info.Id) || _pending.ContainsKey(info.Id))
                {
                    _logger.LogDebug("Watcher re-reported {Device}", LogRedaction.DeviceId(info.Id));
                    return;
                }

                pending = new PendingAdd();
                _pending[info.Id] = pending;
            }

            // DeviceAdded is raised once the display name is resolved (bounded by the resolver's timeout).
            _ = ResolveAndAddAsync(session, info, pending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceWatcher.Added handler failed");
        }
    }

    private async Task ResolveAndAddAsync(Session session, DeviceInformation info, PendingAdd pending)
    {
        try
        {
            var resolved = await BluetoothDeviceNameResolver.ResolveAsync(info, session.Cancellation.Token).ConfigureAwait(false);

            PhoneDevice? added = null;
            lock (_sync)
            {
                // Dropped if the watcher was stopped/restarted or the device was removed meanwhile.
                if (ReferenceEquals(_session, session)
                    && _pending.TryGetValue(info.Id, out var current)
                    && ReferenceEquals(current, pending))
                {
                    _pending.Remove(info.Id);
                    added = ToPhoneDevice(info, resolved.Name);
                    _devices[info.Id] = new Entry(info, added);
                }
            }

            if (added is null)
            {
                return;
            }

            if (resolved.Source == DeviceNameSource.BluetoothDevice)
            {
                _logger.LogInformation(
                    "Watcher Added \"{Name}\" {Device} (name from Bluetooth device; interface reported \"{InterfaceName}\")",
                    added.Name,
                    LogRedaction.DeviceId(added.Id),
                    info.Name);
            }
            else
            {
                _logger.LogWarning(
                    "Watcher Added \"{Name}\" {Device} (name from A2DP interface: {Reason})",
                    added.Name,
                    LogRedaction.DeviceId(added.Id),
                    resolved.Reason);
            }

            SafeEvent.Raise(DeviceAdded, this, new PhoneDeviceEventArgs(added), _logger, nameof(DeviceAdded));
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Watcher stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolving the device name failed");
        }
        finally
        {
            pending.Done.TrySetResult();
        }
    }

    private void OnUpdated(Session session, DeviceInformationUpdate update)
    {
        try
        {
            PhoneDevice? forwarded = null;
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session) || !_devices.TryGetValue(update.Id, out var entry))
                {
                    return;
                }

                // Forwarded as-is (Phase 0 saw Name="4" here); the stored name stays the one from Added.
                entry.Info.Update(update);
                forwarded = ToPhoneDevice(entry.Info);
                _devices[update.Id] = entry with { Device = entry.Device with { IsConnected = forwarded.IsConnected } };
            }

            _logger.LogDebug(
                "Watcher Updated {Device} Name=\"{Name}\" IsConnected={IsConnected}",
                LogRedaction.DeviceId(forwarded.Id),
                forwarded.Name,
                forwarded.IsConnected);
            SafeEvent.Raise(DeviceUpdated, this, new PhoneDeviceEventArgs(forwarded), _logger, nameof(DeviceUpdated));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceWatcher.Updated handler failed");
        }
    }

    private void OnRemoved(Session session, DeviceInformationUpdate update)
    {
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                _pending.Remove(update.Id); // never reported as Added, so nothing to report as Removed
                _unconfirmed?.Remove(update.Id);
                if (!_devices.Remove(update.Id))
                {
                    return;
                }
            }

            _logger.LogInformation("Watcher Removed {Device}", LogRedaction.DeviceId(update.Id));
            SafeEvent.Raise(DeviceRemoved, this, new PhoneDeviceRemovedEventArgs(update.Id), _logger, nameof(DeviceRemoved));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceWatcher.Removed handler failed");
        }
    }

    private void OnEnumerationCompleted(Session session)
    {
        try
        {
            Task[] pending;
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                pending = _pending.Values.Select(p => (Task)p.Done.Task).ToArray();
            }

            _ = CompleteEnumerationAsync(session, pending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceWatcher.EnumerationCompleted handler failed");
        }
    }

    /// <summary>Runs after pending name lookups finish, so every device is reported before EnumerationCompleted.</summary>
    private async Task CompleteEnumerationAsync(Session session, Task[] pendingAdds)
    {
        try
        {
            await Task.WhenAll(pendingAdds).ConfigureAwait(false);

            List<string> stale;
            int count;
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                _enumerationCompleted = true;
                _restartFailures = 0;
                stale = _unconfirmed?.ToList() ?? [];
                _unconfirmed = null;
                foreach (var id in stale)
                {
                    _devices.Remove(id);
                }

                count = _devices.Count;
            }

            foreach (var id in stale)
            {
                _logger.LogInformation("{Device} not re-reported after watcher restart; removed", LogRedaction.DeviceId(id));
                SafeEvent.Raise(DeviceRemoved, this, new PhoneDeviceRemovedEventArgs(id), _logger, nameof(DeviceRemoved));
            }

            _logger.LogInformation("Watcher EnumerationCompleted: {Count} device(s)", count);
            SafeEvent.Raise(EnumerationCompleted, this, _logger, nameof(EnumerationCompleted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completing the device enumeration failed");
        }
    }

    private void OnStopped(Session session)
    {
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                var status = session.Watcher.Status;
                if (status == DeviceWatcherStatus.Aborted && _running)
                {
                    _logger.LogWarning("A2DP source watcher aborted");
                    StopSession_NoLock();
                    ScheduleRestart_NoLock();
                }
                else
                {
                    _logger.LogInformation("A2DP source watcher stopped with status {Status}", status);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceWatcher.Stopped handler failed");
        }
    }

    private sealed record Entry(DeviceInformation Info, PhoneDevice Device);

    /// <summary>A device seen by Added whose display name is still being resolved.</summary>
    private sealed class PendingAdd
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// One DeviceWatcher plus its handlers. Handlers compare sessions by reference, not the WinRT sender,
    /// so events from an old (stopped/aborted) watcher are ignored reliably.
    /// </summary>
    private sealed class Session
    {
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformation> _added;
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> _updated;
        private readonly TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> _removed;
        private readonly TypedEventHandler<DeviceWatcher, object> _completed;
        private readonly TypedEventHandler<DeviceWatcher, object> _stopped;

        public Session(A2dpSourceWatcher owner, DeviceWatcher watcher)
        {
            Watcher = watcher;
            _added = (_, info) => owner.OnAdded(this, info);
            _updated = (_, update) => owner.OnUpdated(this, update);
            _removed = (_, update) => owner.OnRemoved(this, update);
            _completed = (_, _) => owner.OnEnumerationCompleted(this);
            _stopped = (_, _) => owner.OnStopped(this);
        }

        public DeviceWatcher Watcher { get; }

        /// <summary>Cancelled (never disposed) when the session is detached; aborts its name lookups.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public void Attach()
        {
            Watcher.Added += _added;
            Watcher.Updated += _updated;
            Watcher.Removed += _removed;
            Watcher.EnumerationCompleted += _completed;
            Watcher.Stopped += _stopped;
        }

        public void Detach(ILogger logger)
        {
            Cancellation.Cancel();
            try
            {
                Watcher.Added -= _added;
                Watcher.Updated -= _updated;
                Watcher.Removed -= _removed;
                Watcher.EnumerationCompleted -= _completed;
                Watcher.Stopped -= _stopped;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unsubscribing DeviceWatcher events failed");
            }
        }
    }
}

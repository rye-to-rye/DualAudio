using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>
/// Read-only Bluetooth radio state (Windows.Devices.Radios). A DeviceWatcher on
/// <see cref="Radio.GetDeviceSelector"/> tracks radios being added/removed (USB adapter unplugged), and each
/// Bluetooth radio's StateChanged tracks On/Off. No Bluetooth radio -> <see cref="RadioStatus.NotPresent"/>.
/// <para><see cref="StatusChanged"/> is raised on the WinRT callback thread, only when the aggregate changes.</para>
/// </summary>
public sealed class RadioMonitor : IRadioMonitor
{
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();

    // Guarded by _sync.
    private readonly Dictionary<string, RadioEntry> _radios = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _pendingAdds = [];
    private HashSet<string>? _unconfirmed; // radios a recreated watcher has not re-reported yet
    private int _restartFailures;
    private DeviceWatcher? _watcher;
    private TypedEventHandler<DeviceWatcher, DeviceInformation>? _added;
    private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>? _removed;
    private TypedEventHandler<DeviceWatcher, object>? _completed;
    private TypedEventHandler<DeviceWatcher, object>? _stopped;
    private RadioStatus _status = RadioStatus.Unknown;
    private bool _initialized;
    private bool _started;
    private bool _disposed;

    public RadioMonitor(ILogger<RadioMonitor>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<RadioStatusChangedEventArgs>? StatusChanged;

    public RadioStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Starts monitoring and waits (bounded) for the initial radio enumeration, so <see cref="Status"/> is
    /// meaningful when this returns. Idempotent. Never throws for WinRT failures.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                StartWatcher_NoLock();
            }
        }

        try
        {
            await _ready.Task.WaitAsync(EnumerationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Radio enumeration did not complete within {Timeout}; status stays {Status}", EnumerationTimeout, Status);
        }
    }

    public void Dispose()
    {
        DeviceWatcher? watcher;
        List<RadioEntry> radios;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            radios = [.. _radios.Values];
            _radios.Clear();
            _present.Clear();
        }

        _lifetime.Cancel();
        if (watcher is not null)
        {
            try
            {
                watcher.Added -= _added;
                watcher.Removed -= _removed;
                watcher.EnumerationCompleted -= _completed;
                watcher.Stopped -= _stopped;
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping the radio watcher failed");
            }
        }

        foreach (var radio in radios)
        {
            radio.Detach(_logger);
        }

        _ready.TrySetResult();
    }

    /// <summary>Aggregate of all Bluetooth radios: none -> NotPresent; any On -> On; else Off, Disabled, Unknown.</summary>
    internal static RadioStatus Aggregate(IReadOnlyCollection<RadioState> states)
    {
        if (states.Count == 0)
        {
            return RadioStatus.NotPresent;
        }

        if (states.Contains(RadioState.On))
        {
            return RadioStatus.On;
        }

        if (states.Contains(RadioState.Off))
        {
            return RadioStatus.Off;
        }

        return states.Contains(RadioState.Disabled) ? RadioStatus.Disabled : RadioStatus.Unknown;
    }

    // ---------------------------------------------------------------- watcher

    private void StartWatcher_NoLock()
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(Radio.GetDeviceSelector());
            _added = (_, info) => OnRadioAdded(watcher, info);
            _removed = (_, update) => OnRadioRemoved(watcher, update);
            _completed = (_, _) => OnEnumerationCompleted(watcher);
            _stopped = (_, _) => OnWatcherStopped(watcher);
            watcher.Added += _added;
            watcher.Removed += _removed;
            watcher.EnumerationCompleted += _completed;
            watcher.Stopped += _stopped;
            _watcher = watcher;
            watcher.Start();
        }
        catch (Exception ex)
        {
            // No hot-plug tracking, but still read the radios once.
            _watcher = null;
            _unconfirmed = null;
            if (_radios.Count == 0)
            {
                _logger.LogError(ex, "Radio watcher could not start; falling back to a one-time Radio.GetRadiosAsync");
                _ = FallbackSnapshotAsync();
            }
            else
            {
                // Recreating after a stop failed: the radios already tracked keep reporting their state.
                _logger.LogError(ex, "Radio watcher could not be recreated; adapter hot-plug is no longer tracked");
            }
        }
    }

    private bool IsCurrent(DeviceWatcher watcher) => ReferenceEquals(_watcher, watcher) && !_disposed;

    private void OnRadioAdded(DeviceWatcher watcher, DeviceInformation info)
    {
        try
        {
            lock (_sync)
            {
                if (!IsCurrent(watcher))
                {
                    return;
                }

                _present.Add(info.Id);
                _unconfirmed?.Remove(info.Id);
                if (_radios.ContainsKey(info.Id))
                {
                    return; // re-reported by a recreated watcher; already tracked
                }

                _pendingAdds.RemoveAll(t => t.IsCompleted);
                _pendingAdds.Add(AddRadioAsync(info.Id));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio watcher Added handler failed");
        }
    }

    private async Task AddRadioAsync(string id)
    {
        try
        {
            var radio = await Radio.FromIdAsync(id).AsTask(_lifetime.Token).ConfigureAwait(false);
            if (radio is null || radio.Kind != RadioKind.Bluetooth)
            {
                return;
            }

            var entry = new RadioEntry(this, id, radio);
            lock (_sync)
            {
                // Removed (or disposed) while FromIdAsync was running.
                if (_disposed || !_present.Contains(id))
                {
                    return;
                }

                _radios[id] = entry;
                entry.Attach();
            }

            _logger.LogInformation("Bluetooth radio \"{Name}\" added ({State})", entry.ReadName(), entry.ReadState(_logger));
            Recompute("radio added");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Radio.FromIdAsync failed");
        }
    }

    private void OnRadioRemoved(DeviceWatcher watcher, DeviceInformationUpdate update)
    {
        try
        {
            RadioEntry? entry;
            lock (_sync)
            {
                if (!IsCurrent(watcher))
                {
                    return;
                }

                _present.Remove(update.Id);
                _radios.Remove(update.Id, out entry);
            }

            if (entry is null)
            {
                return;
            }

            entry.Detach(_logger);
            _logger.LogWarning("Bluetooth radio removed (adapter unplugged or disabled)");
            Recompute("radio removed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio watcher Removed handler failed");
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher watcher)
    {
        try
        {
            Task[] pending;
            lock (_sync)
            {
                if (!IsCurrent(watcher))
                {
                    return;
                }

                pending = [.. _pendingAdds];
            }

            _ = CompleteInitializationAsync(pending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio watcher EnumerationCompleted handler failed");
        }
    }

    private async Task CompleteInitializationAsync(Task[] pendingAdds)
    {
        try
        {
            // AddRadioAsync never throws; this just waits for Radio.FromIdAsync of the initial radios.
            await Task.WhenAll(pendingAdds).ConfigureAwait(false);
            List<RadioEntry> gone = [];
            lock (_sync)
            {
                _initialized = true;
                _restartFailures = 0;
                foreach (var id in _unconfirmed ?? [])
                {
                    _present.Remove(id);
                    if (_radios.Remove(id, out var entry))
                    {
                        gone.Add(entry);
                    }
                }

                _unconfirmed = null;
            }

            foreach (var entry in gone)
            {
                entry.Detach(_logger);
                _logger.LogWarning("Bluetooth radio not re-reported after the watcher was recreated; removed");
            }

            Recompute("enumeration completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completing radio enumeration failed");
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    private void OnWatcherStopped(DeviceWatcher watcher)
    {
        try
        {
            TimeSpan delay;
            lock (_sync)
            {
                if (!IsCurrent(watcher))
                {
                    return;
                }

                // Dispose() clears _watcher before stopping it, so this stop was not ours (aborted, driver restart).
                DetachWatcher_NoLock(watcher);
                _watcher = null;
                delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, _restartFailures), MaxRestartDelay.TotalSeconds));
                _restartFailures++;
            }

            // Radios already tracked keep reporting StateChanged meanwhile; only add/remove tracking pauses.
            _logger.LogWarning("Radio watcher stopped unexpectedly (status {Status}); recreating it in {Delay}", watcher.Status, delay);
            _ = RestartWatcherLaterAsync(delay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio watcher Stopped handler failed");
        }
    }

    private async Task RestartWatcherLaterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || _watcher is not null)
                {
                    return;
                }

                // Radios tracked now must be re-reported by the new watcher's enumeration, or they are dropped.
                _unconfirmed = new HashSet<string>(_radios.Keys, StringComparer.OrdinalIgnoreCase);
                StartWatcher_NoLock();
            }

            _logger.LogInformation("Radio watcher recreated");
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recreating the radio watcher failed");
        }
    }

    private void DetachWatcher_NoLock(DeviceWatcher watcher)
    {
        try
        {
            watcher.Added -= _added;
            watcher.Removed -= _removed;
            watcher.EnumerationCompleted -= _completed;
            watcher.Stopped -= _stopped;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unsubscribing the radio watcher failed");
        }
    }

    private async Task FallbackSnapshotAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(_lifetime.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var index = 0;
                foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
                {
                    var id = "fallback-" + index++;
                    var entry = new RadioEntry(this, id, radio);
                    _present.Add(id);
                    _radios[id] = entry;
                    entry.Attach();
                }

                _initialized = true;
            }

            Recompute("fallback snapshot");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio.GetRadiosAsync failed; Bluetooth status unknown");
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- status

    private void OnRadioStateChanged(RadioEntry entry)
    {
        try
        {
            lock (_sync)
            {
                if (!_radios.TryGetValue(entry.Id, out var current) || !ReferenceEquals(current, entry))
                {
                    return;
                }
            }

            _logger.LogInformation("Bluetooth radio \"{Name}\" state changed -> {State}", entry.ReadName(), entry.ReadState(_logger));
            Recompute("radio state changed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio.StateChanged handler failed");
        }
    }

    private void Recompute(string why)
    {
        RadioStatus previous;
        RadioStatus next;
        lock (_sync)
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            var states = _radios.Values.Select(r => r.ReadState(_logger)).ToList();
            next = Aggregate(states);
            previous = _status;
            if (previous == next)
            {
                return;
            }

            _status = next;
        }

        _logger.LogInformation("Bluetooth status {Previous} -> {Status} ({Why})", previous, next, why);
        SafeEvent.Raise(StatusChanged, this, new RadioStatusChangedEventArgs(next), _logger, nameof(StatusChanged));
    }

    private sealed class RadioEntry
    {
        private readonly Radio _radio;
        private readonly TypedEventHandler<Radio, object> _stateChanged;

        public RadioEntry(RadioMonitor owner, string id, Radio radio)
        {
            Id = id;
            _radio = radio;
            _stateChanged = (_, _) => owner.OnRadioStateChanged(this);
        }

        public string Id { get; }

        public void Attach() => _radio.StateChanged += _stateChanged;

        public void Detach(ILogger logger)
        {
            try
            {
                _radio.StateChanged -= _stateChanged;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unsubscribing Radio.StateChanged failed");
            }
        }

        public RadioState ReadState(ILogger logger)
        {
            try
            {
                return _radio.State;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Reading Radio.State threw HRESULT={HResult}", SafeEvent.HResult(ex.HResult));
                return RadioState.Unknown;
            }
        }

        public string ReadName()
        {
            try
            {
                return _radio.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}

using DualAudio.Core.Abstractions;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Connection;

/// <summary>
/// Owns the connection state machine (docs/architecture.md). Every transition runs through one
/// <see cref="SemaphoreSlim"/>, so double-clicks and platform events arriving on random threads cannot race.
/// Public methods never throw for runtime failures; failures become <see cref="ConnectionState.Error"/>.
/// <para>Transitions are decided under the lock, but <see cref="StateChanged"/> is raised only after the lock
/// is released, in transition order, so handlers may call <see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/>.
/// Events are raised on a thread pool or caller thread; handlers marshal to the UI thread themselves.</para>
/// </summary>
public sealed class ConnectionCoordinator : IAsyncDisposable
{
    private readonly IBluetoothAudioSourceWatcher _watcher;
    private readonly IAudioReceiver _receiver;
    private readonly IRadioMonitor _radio;
    private readonly IClock _clock;
    private readonly ConnectionCoordinatorOptions _options;
    private readonly ILogger _logger;
    private readonly ReconnectPolicy _reconnect;
    private readonly DeviceRegistry _devices = new();
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Guards the fields below that are touched from event threads outside the transition lock.
    private readonly object _sync = new();
    private readonly List<Task> _inFlight = [];
    private readonly Queue<ConnectionStateChangedEventArgs> _pendingEvents = new();
    private bool _dispatchingEvents;
    private CancellationTokenSource? _openCts;
    private string? _openDeviceId;
    private OpenCancelReason _openCancelReason;

    private volatile Snapshot _snapshot = new(ConnectionState.Idle, null, null);
    private int _intentVersion;
    private int _disposed;
    private bool _autoReconnect;

    // Only touched while holding _transitionLock.
    private string? _targetDeviceId;
    private PhoneDevice? _targetDevice;
    private bool _isReconnecting;
    private bool _resumeWhenRadioOn;
    private int _attemptId;
    private bool _linkLossPending;
    private int _linkLossId;

    public ConnectionCoordinator(
        IBluetoothAudioSourceWatcher watcher,
        IAudioReceiver receiver,
        IRadioMonitor radio,
        IClock clock,
        ConnectionCoordinatorOptions? options = null,
        ILogger<ConnectionCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(clock);

        _watcher = watcher;
        _receiver = receiver;
        _radio = radio;
        _clock = clock;
        _options = options ?? new ConnectionCoordinatorOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _reconnect = new ReconnectPolicy(clock, _options.Reconnect, _logger);
        _autoReconnect = _options.AutoReconnect;

        _watcher.DeviceAdded += OnDeviceAdded;
        _watcher.DeviceUpdated += OnDeviceUpdated;
        _watcher.DeviceRemoved += OnDeviceRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _receiver.StateChanged += OnReceiverStateChanged;
        _radio.StatusChanged += OnRadioStatusChanged;
    }

    private enum OpenCancelReason
    {
        None,
        UserDisconnect,
        DeviceRemoved,
        RadioOff,
        Disposed,
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public ConnectionState CurrentState => _snapshot.State;

    /// <summary>The phone being connected / connected / reconnected; null when idle or after user Disconnect.</summary>
    public PhoneDevice? ActiveDevice => _snapshot.Device;

    public ErrorKind? LastError => _snapshot.Error;

    /// <summary>Devices from the watcher, with names from DeviceAdded only.</summary>
    public IReadOnlyList<PhoneDevice> Devices => _devices.Snapshot();

    public bool IsReconnecting => _reconnect.IsPending;

    public DateTimeOffset? NextReconnectAt => _reconnect.NextAttemptAt;

    /// <summary>Reconnect automatically after an unexpected link loss. Can be changed at runtime.</summary>
    public bool AutoReconnect
    {
        get => Volatile.Read(ref _autoReconnect);
        set => Volatile.Write(ref _autoReconnect, value);
    }

    /// <summary>
    /// Connects to the phone. Completes when the state machine has advanced as far as it can right now
    /// (Opening while waiting for Opened, WaitingForPhone, or Error); observe <see cref="StateChanged"/>.
    /// A repeat call for the device already being connected is ignored.
    /// </summary>
    public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return Enqueue(() => ConnectCoreAsync(deviceId, cancellationToken), cancellationToken);
    }

    /// <summary>User Disconnect: aborts any open attempt, cancels pending reconnects and releases the link.</summary>
    public Task DisconnectAsync()
    {
        // Act immediately (not queued) so a queued reconnect attempt or a slow OpenAsync can't win the race.
        Interlocked.Increment(ref _intentVersion);
        _reconnect.Cancel();
        CancelOpen(OpenCancelReason.UserDisconnect, deviceId: null);
        return Enqueue(DisconnectCoreAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _watcher.DeviceAdded -= OnDeviceAdded;
        _watcher.DeviceUpdated -= OnDeviceUpdated;
        _watcher.DeviceRemoved -= OnDeviceRemoved;
        _watcher.EnumerationCompleted -= OnEnumerationCompleted;
        _receiver.StateChanged -= OnReceiverStateChanged;
        _radio.StatusChanged -= OnRadioStatusChanged;

        Interlocked.Increment(ref _intentVersion);
        _reconnect.Dispose();
        CancelOpen(OpenCancelReason.Disposed, deviceId: null);
        await _lifetime.CancelAsync().ConfigureAwait(false);

        await _transitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseReceiverAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <summary>Test hook: completes when no queued or running transition remains.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_sync)
            {
                _inFlight.RemoveAll(t => t.IsCompleted);
                pending = [.. _inFlight];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private static bool SameDevice(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ErrorKind? RadioError(RadioStatus status) => status switch
    {
        RadioStatus.Off or RadioStatus.Disabled => ErrorKind.BluetoothOff,
        RadioStatus.NotPresent => ErrorKind.NoAdapter,
        _ => null,
    };

    private static bool IsRetryable(ErrorKind kind) =>
        kind is ErrorKind.OpenTimedOut or ErrorKind.DeviceNotFound or ErrorKind.Unknown;

    // ---------------------------------------------------------------- serialization

    private Task Enqueue(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        var task = RunSerializedAsync(operation, cancellationToken);
        lock (_sync)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            if (!task.IsCompleted)
            {
                _inFlight.Add(task);
            }
        }

        return task;
    }

    private async Task RunSerializedAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await operation().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure in connection state machine");
            if (Volatile.Read(ref _disposed) == 0)
            {
                _isReconnecting = false;
                SetState(ConnectionState.Error, ErrorKind.Unknown);
            }
        }
        finally
        {
            _transitionLock.Release();
            DispatchPendingEvents();
        }
    }

    /// <summary>
    /// Raises queued <see cref="StateChanged"/> events outside the transition lock. Only one thread drains at
    /// a time, which keeps events in transition order; a re-entrant call from a handler (e.g. a handler that
    /// calls DisconnectAsync) returns immediately and its events are raised by the outer drain loop.
    /// </summary>
    private void DispatchPendingEvents()
    {
        lock (_sync)
        {
            if (_dispatchingEvents || _pendingEvents.Count == 0)
            {
                return;
            }

            _dispatchingEvents = true;
        }

        while (true)
        {
            ConnectionStateChangedEventArgs args;
            lock (_sync)
            {
                if (!_pendingEvents.TryDequeue(out args!))
                {
                    _dispatchingEvents = false;
                    return;
                }
            }

            RaiseStateChanged(args);
        }
    }

    private void RaiseStateChanged(ConnectionStateChangedEventArgs args)
    {
        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<ConnectionStateChangedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StateChanged subscriber threw");
            }
        }
    }

    /// <summary>Runs <paramref name="operation"/> serialized after <paramref name="delay"/>, unless the user acted meanwhile.</summary>
    private void ScheduleGuarded(TimeSpan delay, Func<Task> operation)
    {
        var version = Volatile.Read(ref _intentVersion);
        var token = _lifetime.Token;
        _ = RunLaterAsync();

        async Task RunLaterAsync()
        {
            try
            {
                await _clock.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Enqueue(() => version == Volatile.Read(ref _intentVersion) ? operation() : Task.CompletedTask)
                .ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- user intents

    private async Task ConnectCoreAsync(string deviceId, CancellationToken cancellationToken)
    {
        var state = _snapshot.State;
        if (SameDevice(deviceId, _targetDeviceId)
            && state is ConnectionState.Connected or ConnectionState.Opening or ConnectionState.WaitingForPhone)
        {
            _logger.LogDebug("Connect to {Device} ignored: already {State}", LogRedaction.DeviceId(deviceId), state);
            return;
        }

        Interlocked.Increment(ref _intentVersion);
        StopReconnecting();
        CancelLinkLoss();
        if (state is ConnectionState.Connected or ConnectionState.Opening)
        {
            await CloseReceiverAsync().ConfigureAwait(false);
        }

        _targetDeviceId = deviceId;
        _targetDevice = _devices.Find(deviceId) ?? new PhoneDevice(deviceId, string.Empty, false);
        _resumeWhenRadioOn = false;
        _logger.LogInformation("Connect requested for {Device}", LogRedaction.DeviceId(deviceId));
        await BeginConnectAsync(isReconnect: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectCoreAsync()
    {
        StopReconnecting();
        CancelLinkLoss();
        _resumeWhenRadioOn = false;
        _targetDeviceId = null;
        _targetDevice = null;

        if (_snapshot.State == ConnectionState.Idle)
        {
            return;
        }

        _logger.LogInformation("Disconnect requested");
        await CloseReceiverAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, null);
    }

    // ---------------------------------------------------------------- connect pipeline

    private async Task BeginConnectAsync(bool isReconnect, CancellationToken cancellationToken)
    {
        var deviceId = _targetDeviceId!;
        SetState(ConnectionState.Enabling, null);

        if (RadioError(_radio.Status) is { } radioError)
        {
            _resumeWhenRadioOn = true;
            Fail(radioError, $"radio is {_radio.Status}");
            return;
        }

        if (!_devices.Contains(deviceId))
        {
            if (!isReconnect && _watcher.IsEnumerationCompleted)
            {
                Fail(ErrorKind.DeviceNotPaired, "device not reported by the watcher");
                return;
            }

            SetState(ConnectionState.WaitingForPhone, null);
            return;
        }

        _targetDevice = _devices.Find(deviceId) ?? _targetDevice;
        await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenAsync(CancellationToken callerToken)
    {
        var deviceId = _targetDeviceId!;
        var attempt = ++_attemptId;
        SetState(ConnectionState.Opening, null);

        using var openCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(openCts.Token);
        lock (_sync)
        {
            _openCts = openCts;
            _openDeviceId = deviceId;
            _openCancelReason = OpenCancelReason.None;
        }

        Task<OpenResult> openTask;
        Task first;
        try
        {
            openTask = _receiver.OpenAsync(deviceId, openCts.Token);
            var timeoutTask = _clock.Delay(_options.OpenTimeout, timeoutCts.Token);
            first = await Task.WhenAny(openTask, timeoutTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ClearOpen();
            _logger.LogError(ex, "OpenAsync threw synchronously");
            await CloseReceiverAsync().ConfigureAwait(false);
            Fail(ErrorKind.Unknown, ex.GetType().Name);
            return;
        }

        var reason = ClearOpen();

        if (first != openTask || openTask.IsCanceled)
        {
            var timedOut = first != openTask && first.IsCompletedSuccessfully;
            await openCts.CancelAsync().ConfigureAwait(false);
            ObserveAbandoned(openTask);
            await CloseReceiverAsync().ConfigureAwait(false);
            HandleAbortedOpen(reason, timedOut, callerToken);
            return;
        }

        await timeoutCts.CancelAsync().ConfigureAwait(false);

        if (openTask.IsFaulted)
        {
            _logger.LogError(openTask.Exception, "OpenAsync faulted");
            await CloseReceiverAsync().ConfigureAwait(false);
            Fail(ErrorKind.Unknown, openTask.Exception?.InnerException?.GetType().Name);
            return;
        }

        var result = openTask.Result;
        if (!result.Success)
        {
            await CloseReceiverAsync().ConfigureAwait(false);
            Fail(result.ErrorKind ?? ErrorKind.Unknown, result.Detail);
            return;
        }

        // Phase 0: State still reads Closed here; StateChanged -> Opened follows ~1 ms later.
        _logger.LogInformation("OpenAsync succeeded for {Device}; waiting for Opened", LogRedaction.DeviceId(deviceId));
        ScheduleGuarded(_options.OpenedConfirmationTimeout, () => ConfirmOpenedAsync(attempt));
    }

    private OpenCancelReason ClearOpen()
    {
        lock (_sync)
        {
            var reason = _openCancelReason;
            _openCts = null;
            _openDeviceId = null;
            _openCancelReason = OpenCancelReason.None;
            return reason;
        }
    }

    private void HandleAbortedOpen(OpenCancelReason reason, bool timedOut, CancellationToken callerToken)
    {
        switch (reason)
        {
            case OpenCancelReason.UserDisconnect:
            case OpenCancelReason.Disposed:
                // The queued Disconnect (or disposal) decides the final state.
                return;
            case OpenCancelReason.RadioOff:
                _resumeWhenRadioOn = true;
                Fail(RadioError(_radio.Status) ?? ErrorKind.BluetoothOff, "radio turned off during open");
                return;
            case OpenCancelReason.DeviceRemoved:
                FailAfterLoss("device removed during open");
                return;
        }

        if (timedOut)
        {
            Fail(ErrorKind.OpenTimedOut, $"no result within {_options.OpenTimeout}");
        }
        else if (callerToken.IsCancellationRequested)
        {
            _logger.LogInformation("Connect cancelled by caller");
            SetState(ConnectionState.Disconnected, null);
        }
        else
        {
            Fail(ErrorKind.Unknown, "open was cancelled");
        }
    }

    private async Task ConfirmOpenedAsync(int attempt)
    {
        if (_snapshot.State != ConnectionState.Opening || attempt != _attemptId)
        {
            return;
        }

        if (_receiver.State == ReceiverState.Opened)
        {
            _logger.LogWarning("Opened event never arrived but receiver reports Opened; treating as connected");
            MarkConnected();
            return;
        }

        await CloseReceiverAsync().ConfigureAwait(false);
        Fail(ErrorKind.OpenTimedOut, "Opened event did not arrive");
    }

    private void MarkConnected()
    {
        StopReconnecting();
        _resumeWhenRadioOn = false;
        _targetDevice = _devices.Find(_targetDeviceId!) ?? _targetDevice;
        SetState(ConnectionState.Connected, null);
    }

    /// <summary>
    /// Records a failure. While auto-reconnecting, retryable errors go back to Disconnected and the next
    /// attempt is scheduled; everything else (or an exhausted policy) ends in Error.
    /// </summary>
    private void Fail(ErrorKind kind, string? detail)
    {
        _logger.LogWarning(
            "Connection to {Device} failed: {Kind} ({Detail})",
            LogRedaction.DeviceId(_targetDeviceId),
            kind,
            detail ?? "-");

        if (_isReconnecting && IsRetryable(kind) && !_reconnect.IsExhausted)
        {
            SetState(ConnectionState.Disconnected, kind);
            ScheduleNextReconnect();
            return;
        }

        StopReconnecting();
        SetState(ConnectionState.Error, kind);
    }

    /// <summary>Failure caused by the link/device going away: Bluetooth off wins over "not found".</summary>
    private void FailAfterLoss(string detail)
    {
        if (RadioError(_radio.Status) is { } radioError)
        {
            _resumeWhenRadioOn = true;
            Fail(radioError, detail);
        }
        else
        {
            Fail(ErrorKind.DeviceNotFound, detail);
        }
    }

    // ---------------------------------------------------------------- link loss + reconnect

    private void BeginLinkLoss(string why)
    {
        if (_linkLossPending)
        {
            return;
        }

        _linkLossPending = true;
        var id = ++_linkLossId;
        _logger.LogWarning("Link to {Device} lost? ({Why}); settling", LogRedaction.DeviceId(_targetDeviceId), why);
        ScheduleGuarded(_options.LinkLossSettleWindow, () => CompleteLinkLossAsync(id));
    }

    private void CancelLinkLoss()
    {
        _linkLossPending = false;
        _linkLossId++;
    }

    private async Task CompleteLinkLossAsync(int id)
    {
        if (!_linkLossPending || id != _linkLossId || _snapshot.State != ConnectionState.Connected)
        {
            return;
        }

        _linkLossPending = false;
        await CloseReceiverAsync().ConfigureAwait(false);

        if (RadioError(_radio.Status) is { } radioError)
        {
            _resumeWhenRadioOn = AutoReconnect;
            SetState(ConnectionState.Error, radioError);
            return;
        }

        _logger.LogWarning("Connection lost (BUG-002 idle drop or phone Bluetooth off)");
        SetState(ConnectionState.Disconnected, null);
        if (AutoReconnect)
        {
            _isReconnecting = true;
            ScheduleNextReconnect();
        }
    }

    private void ScheduleNextReconnect()
    {
        var version = Volatile.Read(ref _intentVersion);
        if (!_reconnect.TrySchedule(() => Enqueue(() => ReconnectAttemptAsync(version))))
        {
            _isReconnecting = false;
            _logger.LogWarning("Giving up reconnecting");
        }
    }

    private async Task ReconnectAttemptAsync(int version)
    {
        if (version != Volatile.Read(ref _intentVersion)
            || !_isReconnecting
            || _targetDeviceId is null
            || _snapshot.State != ConnectionState.Disconnected)
        {
            return;
        }

        await BeginConnectAsync(isReconnect: true, CancellationToken.None).ConfigureAwait(false);
    }

    private void StopReconnecting()
    {
        _isReconnecting = false;
        _reconnect.Cancel();
    }

    // ---------------------------------------------------------------- platform events

    private void OnDeviceAdded(object? sender, PhoneDeviceEventArgs e)
    {
        _devices.Add(e.Device);
        _ = Enqueue(() => HandleDeviceAddedAsync(e.Device));
    }

    private void OnDeviceUpdated(object? sender, PhoneDeviceEventArgs e)
    {
        // Deliberately ignored: Phase 0 saw Name="4" and IsConnected=n/a here after a link drop.
        _logger.LogTrace("Watcher Updated for {Device} ignored", LogRedaction.DeviceId(e.Device.Id));
    }

    private void OnDeviceRemoved(object? sender, PhoneDeviceRemovedEventArgs e)
    {
        _devices.Remove(e.DeviceId);
        CancelOpen(OpenCancelReason.DeviceRemoved, e.DeviceId);
        _ = Enqueue(() => HandleDeviceRemovedAsync(e.DeviceId));
    }

    private void OnEnumerationCompleted(object? sender, EventArgs e) =>
        _ = Enqueue(() =>
        {
            if (_snapshot.State == ConnectionState.WaitingForPhone
                && !_isReconnecting
                && !_resumeWhenRadioOn
                && _targetDeviceId is not null
                && !_devices.Contains(_targetDeviceId))
            {
                Fail(ErrorKind.DeviceNotPaired, "not found after enumeration");
            }

            return Task.CompletedTask;
        });

    private void OnReceiverStateChanged(object? sender, ReceiverStateChangedEventArgs e) =>
        _ = Enqueue(() =>
        {
            HandleReceiverState(e.DeviceId, e.State);
            return Task.CompletedTask;
        });

    private void OnRadioStatusChanged(object? sender, RadioStatusChangedEventArgs e)
    {
        if (RadioError(e.Status) is not null)
        {
            CancelOpen(OpenCancelReason.RadioOff, deviceId: null);
        }

        _ = Enqueue(() => HandleRadioAsync(e.Status));
    }

    private async Task HandleDeviceAddedAsync(PhoneDevice device)
    {
        if (!SameDevice(device.Id, _targetDeviceId))
        {
            return;
        }

        _targetDevice = device;
        if (_snapshot.State == ConnectionState.WaitingForPhone)
        {
            await OpenAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleDeviceRemovedAsync(string deviceId)
    {
        if (!SameDevice(deviceId, _targetDeviceId))
        {
            return;
        }

        switch (_snapshot.State)
        {
            case ConnectionState.Connected:
                BeginLinkLoss("device removed");
                break;
            case ConnectionState.Opening:
                // OpenAsync already returned Success; Opened hasn't arrived and now the device is gone.
                await CloseReceiverAsync().ConfigureAwait(false);
                FailAfterLoss("device removed before Opened");
                break;
        }
    }

    private void HandleReceiverState(string deviceId, ReceiverState receiverState)
    {
        var state = _snapshot.State;
        if (!SameDevice(deviceId, _targetDeviceId))
        {
            _logger.LogDebug("Receiver {ReceiverState} for non-active device ignored", receiverState);
            return;
        }

        if (receiverState == ReceiverState.Opened && state == ConnectionState.Opening)
        {
            MarkConnected();
        }
        else if (receiverState == ReceiverState.Closed && state == ConnectionState.Connected)
        {
            // Not user-requested (a user Disconnect clears the target first). Pausing does not fire Closed.
            BeginLinkLoss("receiver Closed");
        }
        else
        {
            _logger.LogDebug("Receiver {ReceiverState} ignored in state {State}", receiverState, state);
        }
    }

    private async Task HandleRadioAsync(RadioStatus status)
    {
        var snapshot = _snapshot;
        _logger.LogInformation("Bluetooth radio is {Status}", status);

        if (RadioError(status) is { } radioError)
        {
            if (_targetDeviceId is null
                || snapshot.Error is ErrorKind.BluetoothOff or ErrorKind.NoAdapter
                || (snapshot.State == ConnectionState.Disconnected && !_isReconnecting))
            {
                return;
            }

            // Collapses Closed + radio Off + Removed (all within ~140 ms) into this single transition.
            var resume = snapshot.State != ConnectionState.Connected || AutoReconnect;
            CancelLinkLoss();
            StopReconnecting();
            await CloseReceiverAsync().ConfigureAwait(false);
            _resumeWhenRadioOn = resume;
            SetState(ConnectionState.Error, radioError);
            return;
        }

        if (status == RadioStatus.On
            && snapshot.State == ConnectionState.Error
            && snapshot.Error is ErrorKind.BluetoothOff or ErrorKind.NoAdapter)
        {
            if (_resumeWhenRadioOn && _targetDeviceId is not null)
            {
                _resumeWhenRadioOn = false;
                _isReconnecting = true;
                await BeginConnectAsync(isReconnect: true, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _targetDeviceId = null;
                _targetDevice = null;
                SetState(ConnectionState.Idle, null);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private void CancelOpen(OpenCancelReason reason, string? deviceId)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_openCts is null || (deviceId is not null && !SameDevice(deviceId, _openDeviceId)))
            {
                return;
            }

            _openCancelReason = reason;
            cts = _openCts;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The open attempt finished meanwhile.
        }
    }

    private void ObserveAbandoned(Task<OpenResult> openTask) =>
        _ = openTask.ContinueWith(
            t => _logger.LogDebug("Abandoned OpenAsync finished as {Status}", t.Status),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task CloseReceiverAsync()
    {
        try
        {
            await _receiver.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the receiver failed");
        }
    }

    private void SetState(ConnectionState state, ErrorKind? error)
    {
        var device = state == ConnectionState.Idle || _targetDevice is null
            ? null
            : _targetDevice with { IsConnected = state == ConnectionState.Connected };
        var previous = _snapshot;
        if (previous.State == state && previous.Error == error && previous.Device == device)
        {
            return;
        }

        _snapshot = new Snapshot(state, error, device);
        _logger.LogInformation(
            "State {Previous} -> {State}{Error} ({Device})",
            previous.State,
            state,
            error is null ? string.Empty : $"({error})",
            LogRedaction.DeviceId(device?.Id));

        // Raised by DispatchPendingEvents after the transition lock is released.
        lock (_sync)
        {
            _pendingEvents.Enqueue(new ConnectionStateChangedEventArgs(previous.State, state, error, device));
        }
    }

    private sealed record Snapshot(ConnectionState State, ErrorKind? Error, PhoneDevice? Device);
}

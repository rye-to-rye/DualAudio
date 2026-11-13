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
/// <para>Transitions are decided under the lock, but <see cref="StateChanged"/> is never raised on a thread that
/// holds it, and always in transition order, so handlers may call <see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/>
/// (even blocking). Events are raised after the lock is released or, when a transition awaits a slow platform
/// call (OpenAsync, CloseAsync), right before that await on a pool thread, so intermediate states such as
/// Enabling/Opening are seen while the call runs. Handlers marshal to the UI thread themselves.</para>
/// </summary>
public sealed class ConnectionCoordinator : IAsyncDisposable
{
    private readonly IBluetoothAudioSourceWatcher _watcher;
    private readonly IAudioReceiver _receiver;
    private readonly IAudioSessionController? _sessions;
    private readonly IRadioMonitor _radio;
    private readonly ISystemPowerEvents? _power;
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

    private volatile Snapshot _snapshot = new(ConnectionState.Idle, null, null, false);
    private int _intentVersion;
    private int _disposed;
    private bool _autoReconnect;

    // Only touched while holding _transitionLock.
    private string? _targetDeviceId;
    private PhoneDevice? _targetDevice;
    private bool _isReconnecting;
    private bool _resumeWhenRadioOn;
    private bool _isAutoConnect;
    private int _attemptId;
    private bool _linkLossPending;
    private int _linkLossId;

    // BUG-001 (stale phone session). Reset per user connect; only touched while holding _transitionLock.
    private bool _primedThisConnect;
    private bool _staleAtConnect;
    private bool _safetyNetUsed;
    private int _primingAttempt;
    private bool _reopenPending;
    private int _primingReopenNumber; // 1-based number of the priming reopen in flight; 0 when none

    // Per connection (reset by BeginConnectAsync): the phone session was Active on the default endpoint right before
    // the open that led to Connected, while we held no link (BUG-001 stale signature). Arms the safety net.
    private bool _staleBeforeOpen;

    // Slow background retry after the fast retries ran out. Only touched while holding _transitionLock, except
    // _slowRetryPaused, which the watcher's Updated handler reads first so it doesn't queue work for nothing.
    private bool _slowRetryActive;
    private volatile bool _slowRetryPaused;
    private bool _slowAttemptInFlight;
    private int _slowRetryId;
    private int _slowRetryAttempts;
    private int _slowRetryUnreachable;
    private DateTimeOffset? _lastAttemptEndedAt;

    // Read from any thread (diagnostics / health loop).
    private volatile bool _primingActive;
    private long _lastClosedAtTicks;
    private int _healthCheckId;

    /// <param name="sessions">Optional. When given, enables the BUG-001 stale-session priming cycle, the
    /// post-connect safety net, and the shutdown session check. When null, none of these run.</param>
    public ConnectionCoordinator(
        IBluetoothAudioSourceWatcher watcher,
        IAudioReceiver receiver,
        IRadioMonitor radio,
        IClock clock,
        ConnectionCoordinatorOptions? options = null,
        ILogger<ConnectionCoordinator>? logger = null,
        IAudioSessionController? sessions = null,
        ISystemPowerEvents? power = null)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(clock);

        _watcher = watcher;
        _receiver = receiver;
        _sessions = sessions;
        _radio = radio;
        _power = power;
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
        if (_power is not null)
        {
            _power.Suspending += OnSystemSuspending;
            _power.Resumed += OnSystemResumed;
            _power.SessionLocked += OnSessionLocked;
            _power.SessionUnlocked += OnSessionUnlocked;
            _power.SessionEnding += OnSessionEnding;
        }
    }

    private enum OpenCancelReason
    {
        None,
        UserDisconnect,
        SessionEnding,
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

    /// <summary>
    /// True in Error when DualAudio stopped trying on its own: the reconnect policy ran out of attempts, or an
    /// auto-connect's phone wasn't reported by the watcher. The UI shows a calm "Phone not found — Retry" instead
    /// of an error banner. The phone stays the target: if the watcher (re-)adds it, the coordinator connects.
    /// </summary>
    public bool GaveUp => _snapshot.GaveUp;

    /// <summary>
    /// True when an <see cref="IAudioSessionController"/> was supplied, so the BUG-001 stale-session priming,
    /// the post-connect safety net and the shutdown session check run. The app asserts this at startup.
    /// </summary>
    public bool IsStaleSessionPrimingEnabled => _sessions is not null;

    public DateTimeOffset? NextReconnectAt => _reconnect.NextAttemptAt;

    /// <summary>Diagnostics: the receiver's own State (may differ from <see cref="CurrentState"/> for a moment).</summary>
    public ReceiverState ReceiverState => _receiver.State;

    /// <summary>Diagnostics: a BUG-001 priming cycle (open, close, reopen) is running.</summary>
    public bool IsPriming => _primingActive;

    /// <summary>Diagnostics: the last open found the phone session already Active (stale signature).</summary>
    public bool LastOpenFoundStaleSession => Volatile.Read(ref _staleBeforeOpen);

    /// <summary>Diagnostics: this connection already used its one automatic (safety-net) reopen.</summary>
    public bool AutomaticReopenUsed => Volatile.Read(ref _safetyNetUsed);

    /// <summary>Diagnostics: when the link was last closed (receiver Closed event, or our close of an open link).</summary>
    public DateTimeOffset? LastClosedAt =>
        Interlocked.Read(ref _lastClosedAtTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>Slow background retries are paused after repeated 0x8007001F until the watcher sees the phone again.</summary>
    public bool IsSlowRetryPaused => _slowRetryPaused;

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
        return Enqueue(() => ConnectCoreAsync(deviceId, auto: false, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Connects to the remembered phone without the user watching (start-up, "Phone not found — Retry"). Unlike
    /// <see cref="ConnectAsync"/>: waits in WaitingForPhone until the watcher reports the phone (even after the
    /// initial enumeration), and retryable failures (e.g. phone Bluetooth off, 0x8007001F) are retried with the
    /// <see cref="ReconnectPolicy"/>. When that runs out, or the enumeration completes without the phone, the state is
    /// Error with <see cref="GaveUp"/> set. A user Disconnect ends all of it.
    /// </summary>
    public Task AutoConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return Enqueue(() => ConnectCoreAsync(deviceId, auto: true, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// "Fix audio" (user escape hatch while Connected but silent): closes the link cleanly, waits for the phone session
    /// to go Inactive (at least <see cref="ConnectionCoordinatorOptions.FixAudioMinWait"/>, at most
    /// <see cref="ConnectionCoordinatorOptions.FixAudioMaxWait"/>), then reconnects. Ignored unless Connected.
    /// </summary>
    public Task FixAudioAsync()
    {
        _logger.LogInformation("user requested audio fix");
        Interlocked.Increment(ref _intentVersion);
        return Enqueue(FixAudioCoreAsync);
    }

    /// <summary>
    /// Logoff/shutdown: closes the connection now, so Windows doesn't end the process with the link open. If the
    /// process is still alive <see cref="ConnectionCoordinatorOptions.SessionEndVetoReconnectDelay"/> later, the
    /// logoff was cancelled and the coordinator reconnects.
    /// </summary>
    public Task CloseForSessionEndAsync()
    {
        Interlocked.Increment(ref _intentVersion);
        _reconnect.Cancel();
        CancelOpen(OpenCancelReason.SessionEnding, deviceId: null);
        return Enqueue(SessionEndCoreAsync);
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
        if (_power is not null)
        {
            _power.Suspending -= OnSystemSuspending;
            _power.Resumed -= OnSystemResumed;
            _power.SessionLocked -= OnSessionLocked;
            _power.SessionUnlocked -= OnSessionUnlocked;
            _power.SessionEnding -= OnSessionEnding;
        }

        Interlocked.Increment(ref _intentVersion);
        _reconnect.Dispose();
        CancelOpen(OpenCancelReason.Disposed, deviceId: null);
        await _lifetime.CancelAsync().ConfigureAwait(false);

        await _transitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Close and dispose the connection here, awaited, so it is released before the process exits
            // (BUG-001: a pipeline left Active by the previous process breaks the next start's first open).
            var hadLink = _snapshot.State is ConnectionState.Connected or ConnectionState.Opening
                          || _receiver.State == ReceiverState.Opened;
            await CloseReceiverAsync().ConfigureAwait(false);
            if (hadLink)
            {
                await LogPhoneSessionAfterCloseAsync().ConfigureAwait(false);
            }
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
        kind is ErrorKind.OpenTimedOut or ErrorKind.DeviceNotFound or ErrorKind.Unknown or ErrorKind.PhoneUnreachable;

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

    private async Task ConnectCoreAsync(string deviceId, bool auto, CancellationToken cancellationToken)
    {
        var state = _snapshot.State;
        if (SameDevice(deviceId, _targetDeviceId)
            && state is ConnectionState.Connected or ConnectionState.Opening or ConnectionState.WaitingForPhone)
        {
            _logger.LogDebug("Connect to {Device} ignored: already {State}", LogRedaction.DeviceId(deviceId), state);
            return;
        }

        // An auto-connect never interrupts a reconnect that is already under way for the same phone.
        if (auto && SameDevice(deviceId, _targetDeviceId) && state == ConnectionState.Disconnected && _isReconnecting)
        {
            _logger.LogDebug("Auto-connect to {Device} ignored: already reconnecting", LogRedaction.DeviceId(deviceId));
            return;
        }

        Interlocked.Increment(ref _intentVersion);
        StopReconnecting();
        StopSlowRetry();
        CancelLinkLoss();
        if (state is ConnectionState.Connected or ConnectionState.Opening)
        {
            await CloseReceiverAsync().ConfigureAwait(false);
        }

        _targetDeviceId = deviceId;
        _targetDevice = _devices.Find(deviceId) ?? new PhoneDevice(deviceId, string.Empty, false);
        _resumeWhenRadioOn = false;
        _primedThisConnect = false;
        _staleAtConnect = false;
        _safetyNetUsed = false;
        _primingAttempt = 0;
        _primingReopenNumber = 0;
        _isAutoConnect = auto;
        _logger.LogInformation("{Kind} requested for {Device}", auto ? "Auto-connect" : "Connect", LogRedaction.DeviceId(deviceId));

        // Auto: behave like a reconnect from the start (wait for the watcher, retry with backoff).
        _isReconnecting = auto;
        await BeginConnectAsync(isReconnect: auto, cancellationToken).ConfigureAwait(false);

        if (auto && _snapshot.State == ConnectionState.WaitingForPhone && _watcher.IsEnumerationCompleted && !_devices.Contains(deviceId))
        {
            // Enumeration already finished without the phone (otherwise OnEnumerationCompleted decides).
            GiveUp(ErrorKind.DeviceNotFound, "remembered phone not reported by the watcher");
        }
    }

    private async Task DisconnectCoreAsync()
    {
        StopReconnecting();
        StopSlowRetry();
        CancelLinkLoss();
        _resumeWhenRadioOn = false;
        _isAutoConnect = false;
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
        _primingReopenNumber = 0;

        // A new connection: it gets its own (at most one) automatic safety-net reopen.
        _safetyNetUsed = false;
        _staleBeforeOpen = false;
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
        _reopenPending = false;
        SetState(ConnectionState.Opening, null);

        using var openCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(openCts.Token);
        lock (_sync)
        {
            _openCts = openCts;
            _openDeviceId = deviceId;
            _openCancelReason = OpenCancelReason.None;
        }

        // Read before every open (not only the first): a reconnect can meet a stale pipeline too, and then the
        // safety net watches the connection it leads to.
        var stale = await IsStalePhoneSessionAsync(openCts.Token).ConfigureAwait(false);
        _staleBeforeOpen = stale;
        if (stale && !_primedThisConnect)
        {
            // BUG-001: this open only primes the pipeline; Connected comes from the reopen after it.
            _primedThisConnect = true;
            _staleAtConnect = true;
            _primingAttempt = attempt;
            _primingActive = true;
            _logger.LogWarning("stale phone session detected; priming reconnect");
        }
        else if (stale)
        {
            _logger.LogWarning("Phone session is already Active before this open (stale signature); the safety net will watch it");
        }

        Task<OpenResult> openTask;
        Task first;
        try
        {
            openTask = _receiver.OpenAsync(deviceId, openCts.Token);
            var timeoutTask = _clock.Delay(_options.OpenTimeout, timeoutCts.Token);
            first = await AwaitFlushingEventsAsync(Task.WhenAny(openTask, timeoutTask)).ConfigureAwait(false);
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
            case OpenCancelReason.SessionEnding:
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
            _logger.LogWarning("Opened event never arrived but receiver reports Opened; treating as opened");
            await OnOpenedAsync().ConfigureAwait(false);
            return;
        }

        await CloseReceiverAsync().ConfigureAwait(false);
        Fail(ErrorKind.OpenTimedOut, "Opened event did not arrive");
    }

    /// <summary>The current attempt reached Opened (state is Opening). Finishes priming or marks Connected.</summary>
    private async Task OnOpenedAsync()
    {
        if (_primingAttempt == 0 || _primingAttempt != _attemptId)
        {
            MarkConnected();
            return;
        }

        // Priming open done: close it and open again. State stays Opening throughout, so the UI never sees
        // Connected -> Disconnected; the Closed event from this close is ignored while Opening.
        _primingAttempt = 0;
        _logger.LogInformation("Priming connection opened; closing it");
        await CloseReceiverAsync().ConfigureAwait(false);
        SchedulePrimingReopen(1, _options.PrimingDelay);
    }

    private int PrimingReopenTotal => 1 + _options.PrimingRetryDelays.Count;

    private void SchedulePrimingReopen(int number, TimeSpan delay)
    {
        _logger.LogInformation("priming reopen attempt {Number}/{Total} in {Delay}s", number, PrimingReopenTotal, delay.TotalSeconds);
        ScheduleReopen(delay, number);
    }

    /// <summary>
    /// Waits <paramref name="delay"/> in Opening, then opens again. The attempt id is bumped so the closed
    /// attempt's confirmation timer is void, and Opened events are ignored until the reopen.
    /// </summary>
    /// <param name="primingReopenNumber">1-based priming reopen number, or 0 for the safety net's reopen.</param>
    private void ScheduleReopen(TimeSpan delay, int primingReopenNumber = 0)
    {
        var attempt = ++_attemptId;
        _reopenPending = true;
        ScheduleGuarded(delay, () => ReopenAsync(attempt, primingReopenNumber));
    }

    /// <summary>Reopen of a priming cycle or of the safety net, unless something else happened meanwhile.</summary>
    private async Task ReopenAsync(int attempt, int primingReopenNumber)
    {
        if (_snapshot.State != ConnectionState.Opening || attempt != _attemptId)
        {
            return;
        }

        _primingReopenNumber = primingReopenNumber;
        await OpenAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A priming reopen failed (receiver already closed). Retryable failures (Unknown, PhoneUnreachable =
    /// 0x8007001F, and OpenTimedOut) are retried while staying in Opening; after the last one the coordinator goes Disconnected
    /// and hands over to the reconnect policy instead of a terminal Error. Returns false if not handled here.
    /// </summary>
    private bool TryHandlePrimingReopenFailure(ErrorKind kind, string? detail)
    {
        var number = _primingReopenNumber;
        _primingReopenNumber = 0;
        if (number == 0 || kind is not (ErrorKind.Unknown or ErrorKind.PhoneUnreachable or ErrorKind.OpenTimedOut))
        {
            return false;
        }

        _logger.LogWarning("Priming reopen {Number}/{Total} failed: {Kind} ({Detail})", number, PrimingReopenTotal, kind, detail ?? "-");
        if (number < PrimingReopenTotal)
        {
            SchedulePrimingReopen(number + 1, _options.PrimingRetryDelays[number - 1]);
            return true;
        }

        _logger.LogWarning("All priming reopens failed; handing over to reconnect");
        StopSlowRetry(); // the fast policy takes over; if it runs out, slow retries start again
        SetState(ConnectionState.Disconnected, kind);
        if (AutoReconnect)
        {
            _isReconnecting = true;
            ScheduleNextReconnect();
        }

        return true;
    }

    private void MarkConnected()
    {
        _primingReopenNumber = 0;
        StopReconnecting();
        StopSlowRetry();
        _resumeWhenRadioOn = false;
        _isAutoConnect = false;
        _targetDevice = _devices.Find(_targetDeviceId!) ?? _targetDevice;
        SetState(ConnectionState.Connected, null);

        // Armed by the stale signature of the open that led here: the priming cycle's (first connect) or this
        // open's own pre-open check (any reconnect: link loss, resume, slow retry, Fix audio). At most one
        // automatic reopen per connection; a silent phone without the stale signature never triggers it.
        var armed = _staleAtConnect || _staleBeforeOpen;
        _staleAtConnect = false;
        if (armed && !_safetyNetUsed && _sessions is not null)
        {
            StartStaleSessionWatch(_attemptId);
        }

        StartHealthCheck();
    }

    // ---------------------------------------------------------------- BUG-001 stale phone session

    /// <summary>
    /// True when, before this open, the default endpoint's phone session is already Active although we hold
    /// no connection: a pipeline left over from a previous process or connection. Checked before every open; the
    /// priming cycle still runs at most once per user connect.
    /// </summary>
    private async Task<bool> IsStalePhoneSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessions is null || _receiver.State == ReceiverState.Opened)
        {
            return false;
        }

        try
        {
            var status = await AwaitFlushingEventsAsync(_sessions.GetPhoneAudioStatusAsync(cancellationToken)).ConfigureAwait(false);
            return status.IsActiveOnDefault;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the phone session before open failed; assuming not stale");
            return false;
        }
    }

    /// <summary>
    /// Safety net: after Connected, watches the phone session for <see cref="ConnectionCoordinatorOptions.StaleSessionWatchWindow"/>.
    /// Fresh activity is a peak above the threshold, or Inactive followed by Active. Without it, reopens once.
    /// Runs outside the transition lock; the reopen itself is serialized and re-checks everything.
    /// </summary>
    private void StartStaleSessionWatch(int attempt)
    {
        var sessions = _sessions!;
        var version = Volatile.Read(ref _intentVersion);
        var token = _lifetime.Token;
        _ = WatchAsync();

        async Task WatchAsync()
        {
            var sawInactive = false;
            var elapsed = TimeSpan.Zero;
            try
            {
                while (elapsed < _options.StaleSessionWatchWindow)
                {
                    await _clock.Delay(_options.StaleSessionPollInterval, token).ConfigureAwait(false);
                    elapsed += _options.StaleSessionPollInterval;
                    if (version != Volatile.Read(ref _intentVersion) || _snapshot.State != ConnectionState.Connected)
                    {
                        return;
                    }

                    PhoneAudioStatus status;
                    try
                    {
                        status = await sessions.GetPhoneAudioStatusAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Safety net: reading the phone session failed");
                        continue;
                    }

                    if (status.Peak > _options.StaleSessionPeakThreshold || (status.IsActiveOnDefault && sawInactive))
                    {
                        _logger.LogDebug("Safety net: phone session shows fresh activity");
                        return;
                    }

                    if (status.Sessions.Any(s => s.Muted))
                    {
                        // Windows reports a muted session Inactive, so "no activity" means nothing while muted.
                        _logger.LogInformation("Safety net: phone audio is muted; not reopening");
                        return;
                    }

                    // Only a real Inactive session on the default endpoint counts. "No session on the default" (e.g. the
                    // default output switched for a moment) must not look like Inactive -> Active.
                    sawInactive |= status.Sessions.Any(s => s.IsOnDefaultEndpoint && !s.IsActive);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Enqueue(() => version == Volatile.Read(ref _intentVersion) ? StaleSessionReopenAsync(attempt) : Task.CompletedTask)
                .ConfigureAwait(false);
        }
    }

    private async Task StaleSessionReopenAsync(int attempt)
    {
        if (_snapshot.State != ConnectionState.Connected || attempt != _attemptId || _safetyNetUsed)
        {
            return;
        }

        _safetyNetUsed = true;
        _logger.LogWarning(
            "Phone session was stale before connect and shows no fresh activity {Window} s after Connected; automatic reopen (once per connection)",
            _options.StaleSessionWatchWindow.TotalSeconds);
        CancelLinkLoss();
        SetState(ConnectionState.Opening, null);
        await CloseReceiverAsync().ConfigureAwait(false);
        ScheduleReopen(_options.PrimingDelay);
    }

    // ---------------------------------------------------------------- "Fix audio" (user)

    private async Task FixAudioCoreAsync()
    {
        if (_snapshot.State != ConnectionState.Connected || _targetDeviceId is null)
        {
            _logger.LogInformation("Audio fix ignored: state is {State}", _snapshot.State);
            return;
        }

        CancelLinkLoss();
        StopReconnecting();
        StopSlowRetry();

        // A fresh start for this phone: the reconnect may prime again and gets its own safety net.
        _primedThisConnect = false;
        _staleAtConnect = false;
        _safetyNetUsed = false;
        _primingAttempt = 0;
        _primingReopenNumber = 0;

        SetState(ConnectionState.Opening, null);
        await CloseReceiverAsync().ConfigureAwait(false);
        var attempt = ++_attemptId;
        _reopenPending = true;
        WaitForSessionInactiveThenReopen(attempt);
    }

    /// <summary>
    /// Outside the transition lock: waits <see cref="ConnectionCoordinatorOptions.FixAudioMinWait"/>, then polls until the
    /// phone session on the default endpoint is no longer Active or <see cref="ConnectionCoordinatorOptions.FixAudioMaxWait"/>
    /// has passed, then reopens (serialized, unless the user acted meanwhile).
    /// </summary>
    private void WaitForSessionInactiveThenReopen(int attempt)
    {
        var version = Volatile.Read(ref _intentVersion);
        var token = _lifetime.Token;
        _ = WaitAsync();

        async Task WaitAsync()
        {
            var waited = _options.FixAudioMinWait;
            var inactive = false;
            try
            {
                await _clock.Delay(_options.FixAudioMinWait, token).ConfigureAwait(false);
                while (version == Volatile.Read(ref _intentVersion))
                {
                    inactive = _sessions is null || !(await ReadStatusSafeAsync(token).ConfigureAwait(false)).IsActiveOnDefault;
                    if (inactive || waited >= _options.FixAudioMaxWait)
                    {
                        break;
                    }

                    await _clock.Delay(_options.StaleSessionPollInterval, token).ConfigureAwait(false);
                    waited += _options.StaleSessionPollInterval;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Audio fix: phone session {Outcome} after {Seconds:0.0} s; reconnecting",
                inactive ? "is Inactive" : "is still Active",
                waited.TotalSeconds);
            await Enqueue(() => version == Volatile.Read(ref _intentVersion) ? FixAudioReopenAsync(attempt) : Task.CompletedTask)
                .ConfigureAwait(false);
        }
    }

    private async Task FixAudioReopenAsync(int attempt)
    {
        if (_snapshot.State != ConnectionState.Opening || attempt != _attemptId)
        {
            return;
        }

        // A failure goes through the reconnect policy (Disconnected + retry), not straight to Error.
        _isReconnecting = true;
        _primingReopenNumber = 0;
        await OpenAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<PhoneAudioStatus> ReadStatusSafeAsync(CancellationToken token)
    {
        try
        {
            return await _sessions!.GetPhoneAudioStatusAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reading the phone session failed");
            return PhoneAudioStatus.Unavailable;
        }
    }

    // ---------------------------------------------------------------- silent state loss

    /// <summary>
    /// While Connected, every <see cref="ConnectionCoordinatorOptions.HealthCheckInterval"/>: if the receiver reads
    /// Closed although no Closed event arrived, treat it as a lost link. One loop per Connected; it ends on its own.
    /// </summary>
    private void StartHealthCheck()
    {
        var id = Interlocked.Increment(ref _healthCheckId);
        var token = _lifetime.Token;
        _ = LoopAsync();

        async Task LoopAsync()
        {
            while (true)
            {
                try
                {
                    await _clock.Delay(_options.HealthCheckInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (id != Volatile.Read(ref _healthCheckId) || _snapshot.State != ConnectionState.Connected)
                {
                    return;
                }

                await Enqueue(() => CheckReceiverHealthAsync(id, "periodic check")).ConfigureAwait(false);
            }
        }
    }

    private Task CheckReceiverHealthAsync(int healthCheckId, string why)
    {
        if (healthCheckId != _healthCheckId || _snapshot.State != ConnectionState.Connected || _linkLossPending)
        {
            return Task.CompletedTask;
        }

        if (_receiver.State == ReceiverState.Closed)
        {
            _logger.LogWarning("Receiver reads Closed but no Closed event arrived ({Why}); treating it as a lost link", why);
            BeginLinkLoss("silent Closed");
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- sleep / lock / logoff

    private void OnSystemSuspending(object? sender, EventArgs e) =>
        _logger.LogInformation("System is going to sleep (state {State})", _snapshot.State);

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        _logger.LogInformation("System resumed from sleep (state {State})", _snapshot.State);

        // Timers from before the sleep (settle window, safety net, confirmations) are void.
        Interlocked.Increment(ref _intentVersion);
        _ = Enqueue(HandleResumeAsync);
    }

    private void OnSessionLocked(object? sender, EventArgs e) =>
        _logger.LogInformation("Session locked (state {State}); nothing to do", _snapshot.State);

    private void OnSessionUnlocked(object? sender, EventArgs e)
    {
        _logger.LogInformation("Session unlocked (state {State}); checking the link", _snapshot.State);
        _ = Enqueue(() => CheckReceiverHealthAsync(_healthCheckId, "unlock"));
    }

    private void OnSessionEnding(object? sender, EventArgs e)
    {
        _logger.LogInformation("Logoff/shutdown starting; closing the connection");
        try
        {
            // Windows ends the process soon after this handler returns, so finish the close here (bounded).
            if (!CloseForSessionEndAsync().Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Closing the connection for logoff/shutdown took longer than 3 s");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the connection for logoff/shutdown failed");
        }
    }

    /// <summary>
    /// After sleep the old AudioPlaybackConnection may be dead without telling us. Release it and reconnect through the
    /// reconnect policy; the pre-open check primes again if the pipeline is stale.
    /// </summary>
    private async Task HandleResumeAsync()
    {
        var snapshot = _snapshot;
        var wanted = _targetDeviceId is not null
                     && (snapshot.State is ConnectionState.Connected or ConnectionState.Opening or ConnectionState.Enabling
                         || (snapshot.State == ConnectionState.Disconnected && _isReconnecting)
                         || snapshot is { State: ConnectionState.Error, GaveUp: true });
        if (!wanted)
        {
            // Idle, user-disconnected, waiting for the watcher, or a Bluetooth error the radio handler resolves.
            _logger.LogInformation("Resume: nothing to reconnect (state {State})", snapshot.State);
            return;
        }

        CancelLinkLoss();
        StopReconnecting();
        StopSlowRetry();
        await CloseReceiverAsync().ConfigureAwait(false);

        _primedThisConnect = false;
        _staleAtConnect = false;
        _primingAttempt = 0;
        _primingReopenNumber = 0;
        _reopenPending = false;
        _attemptId++;

        _logger.LogInformation("Resume: released the old connection; reconnecting");
        SetState(ConnectionState.Disconnected, null);
        if (RadioError(_radio.Status) is not null)
        {
            // Bluetooth isn't back yet: the radio handler resumes once it reports On.
            _resumeWhenRadioOn = true;
            SetState(ConnectionState.Error, RadioError(_radio.Status));
            return;
        }

        _isReconnecting = true;
        ScheduleNextReconnect();
    }

    private async Task SessionEndCoreAsync()
    {
        if (_targetDeviceId is null || _snapshot.State == ConnectionState.Idle)
        {
            return;
        }

        CancelLinkLoss();
        StopReconnecting();
        StopSlowRetry();
        await CloseReceiverAsync().ConfigureAwait(false);
        _logger.LogInformation("Connection closed for logoff/shutdown");
        SetState(ConnectionState.Disconnected, null);

        // Still running this much later: the logoff/shutdown was cancelled, so reconnect.
        ScheduleGuarded(_options.SessionEndVetoReconnectDelay, ReconnectAfterVetoedSessionEndAsync);
    }

    private Task ReconnectAfterVetoedSessionEndAsync()
    {
        if (_targetDeviceId is null || _snapshot.State != ConnectionState.Disconnected || _isReconnecting)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Still running after logoff/shutdown started (it was cancelled); reconnecting");
        _isReconnecting = true;
        ScheduleNextReconnect();
        return Task.CompletedTask;
    }

    /// <summary>After the shutdown close: logs whether the phone session went Inactive (diagnostics only).</summary>
    private async Task LogPhoneSessionAfterCloseAsync()
    {
        if (_sessions is null)
        {
            return;
        }

        _logger.LogInformation("Connection closed and disposed on shutdown");
        var deadline = _clock.UtcNow + _options.ShutdownSessionCheckTimeout;
        try
        {
            while (true)
            {
                var status = await _sessions.GetPhoneAudioStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (!status.IsActiveOnDefault)
                {
                    _logger.LogInformation("Phone session on the default endpoint is Inactive after close");
                    return;
                }

                if (_clock.UtcNow >= deadline)
                {
                    _logger.LogWarning(
                        "Phone session on the default endpoint is still Active {Timeout} s after a clean close (next start may see it as stale)",
                        _options.ShutdownSessionCheckTimeout.TotalSeconds);
                    return;
                }

                await _clock.Delay(_options.StaleSessionPollInterval, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the phone session after close failed");
        }
    }

    /// <summary>
    /// Records a failure. While auto-reconnecting, retryable errors go back to Disconnected and the next
    /// attempt is scheduled; everything else (or an exhausted policy) ends in Error.
    /// </summary>
    private void Fail(ErrorKind kind, string? detail)
    {
        if (TryHandlePrimingReopenFailure(kind, detail))
        {
            return;
        }

        if (_slowAttemptInFlight)
        {
            HandleSlowRetryFailure(kind, detail);
            return;
        }

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

        // Retryable but the policy ran out: DualAudio stopped trying on its own (calm "Phone not found").
        var gaveUp = _isReconnecting && IsRetryable(kind);
        StopReconnecting();
        SetState(ConnectionState.Error, kind, gaveUp);
        if (gaveUp)
        {
            StartSlowRetry("fast retries ended");
        }
    }

    /// <summary>
    /// Stops the fast retries but keeps the phone as the target, so a watcher Added connects again; while the phone
    /// is still reported by the watcher, slow background retries continue.
    /// </summary>
    private void GiveUp(ErrorKind kind, string why)
    {
        _logger.LogWarning("Giving up on {Device} for now ({Why}); waiting for the phone to reappear or Retry", LogRedaction.DeviceId(_targetDeviceId), why);
        StopReconnecting();
        SetState(ConnectionState.Error, kind, gaveUp: true);
        StartSlowRetry(why);
    }

    // ---------------------------------------------------------------- slow background retry

    /// <summary>
    /// After the fast retries ran out: one attempt every <see cref="ReconnectOptions.SlowRetryInterval"/> while the
    /// state is the calm "Phone not found" (Error + GaveUp). Not started for a phone the watcher doesn't report
    /// (a watcher Added handles that one).
    /// </summary>
    private void StartSlowRetry(string why)
    {
        var interval = _options.Reconnect.SlowRetryInterval;
        if (interval <= TimeSpan.Zero || _targetDeviceId is null || !_devices.Contains(_targetDeviceId))
        {
            return;
        }

        if (!_slowRetryActive)
        {
            _slowRetryActive = true;
            _slowRetryPaused = false;
            _slowRetryAttempts = 0;
            _slowRetryUnreachable = 0;
        }

        ScheduleSlowRetry(interval, why);
    }

    private void ScheduleSlowRetry(TimeSpan delay, string why)
    {
        var id = ++_slowRetryId;
        _logger.LogInformation(
            "Slow retry for {Device} in {Delay} s ({Why})",
            LogRedaction.DeviceId(_targetDeviceId),
            delay.TotalSeconds,
            why);
        ScheduleGuarded(delay, () => SlowRetryAttemptAsync(id));
    }

    private void StopSlowRetry()
    {
        if (_slowRetryActive)
        {
            _logger.LogDebug("Slow retries stopped");
        }

        _slowRetryActive = false;
        _slowRetryPaused = false;
        _slowAttemptInFlight = false;
        _slowRetryUnreachable = 0;
        _slowRetryId++;
    }

    private async Task SlowRetryAttemptAsync(int id)
    {
        if (id != _slowRetryId || !_slowRetryActive || _slowRetryPaused || _targetDeviceId is null
            || _snapshot is not { State: ConnectionState.Error, GaveUp: true })
        {
            return;
        }

        if (RadioError(_radio.Status) is not null || !_devices.Contains(_targetDeviceId))
        {
            // The radio handler / a watcher Added takes over from here.
            _logger.LogInformation("Slow retry skipped (radio {Radio}, phone listed {Listed})", _radio.Status, _devices.Contains(_targetDeviceId));
            ScheduleSlowRetry(_options.Reconnect.SlowRetryInterval, "skipped");
            return;
        }

        _slowRetryAttempts++;
        _slowAttemptInFlight = true;
        _logger.LogInformation("Slow retry attempt {Attempt} for {Device}", _slowRetryAttempts, LogRedaction.DeviceId(_targetDeviceId));
        await BeginConnectAsync(isReconnect: true, CancellationToken.None).ConfigureAwait(false);
    }

    private void HandleSlowRetryFailure(ErrorKind kind, string? detail)
    {
        _slowAttemptInFlight = false;
        _lastAttemptEndedAt = _clock.UtcNow;
        _slowRetryUnreachable = kind == ErrorKind.PhoneUnreachable ? _slowRetryUnreachable + 1 : 0;
        _logger.LogWarning("Slow retry attempt {Attempt} failed: {Kind} ({Detail})", _slowRetryAttempts, kind, detail ?? "-");

        if (!IsRetryable(kind))
        {
            StopSlowRetry();
            SetState(ConnectionState.Error, kind);
            return;
        }

        SetState(ConnectionState.Error, kind, gaveUp: true);
        if (_slowRetryUnreachable >= _options.Reconnect.SlowRetryPauseAfterUnreachable)
        {
            _slowRetryPaused = true;
            _logger.LogWarning(
                "Slow retries paused after {Count} consecutive 0x8007001F (phone unreachable); waiting for the watcher to report the phone again or Retry",
                _slowRetryUnreachable);
            return;
        }

        ScheduleSlowRetry(_options.Reconnect.SlowRetryInterval, "previous attempt failed");
    }

    /// <summary>A watcher Updated for the phone resumes paused slow retries (unless our own attempt just caused it).</summary>
    private Task HandleDeviceUpdatedAsync(string deviceId)
    {
        if (!_slowRetryPaused || !_slowRetryActive || !SameDevice(deviceId, _targetDeviceId)
            || _snapshot is not { State: ConnectionState.Error, GaveUp: true })
        {
            return Task.CompletedTask;
        }

        if (_lastAttemptEndedAt is { } ended && _clock.UtcNow - ended < _options.Reconnect.SlowRetryIgnoreUpdatesAfterAttempt)
        {
            _logger.LogDebug("Watcher Updated right after our own attempt; slow retries stay paused");
            return Task.CompletedTask;
        }

        _slowRetryPaused = false;
        ScheduleSlowRetry(_options.Reconnect.SlowRetryResumeDelay, "watcher reported the phone again");
        return Task.CompletedTask;
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
            GiveUp(_snapshot.Error ?? ErrorKind.DeviceNotFound, "reconnect attempts exhausted");
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
        // Metadata is ignored (Phase 0 saw Name="4" and IsConnected=n/a here after a link drop). The event itself only
        // serves as "the phone is around again" for paused slow retries.
        _logger.LogTrace("Watcher Updated for {Device}", LogRedaction.DeviceId(e.Device.Id));
        if (_slowRetryPaused)
        {
            _ = Enqueue(() => HandleDeviceUpdatedAsync(e.Device.Id));
        }
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
            if (_snapshot.State != ConnectionState.WaitingForPhone
                || _resumeWhenRadioOn
                || _targetDeviceId is null
                || _devices.Contains(_targetDeviceId))
            {
                return Task.CompletedTask;
            }

            if (_isAutoConnect)
            {
                // The remembered phone isn't paired (any more?). Calm state; a later Added still connects.
                GiveUp(ErrorKind.DeviceNotFound, "remembered phone not reported by the watcher");
            }
            else if (!_isReconnecting)
            {
                Fail(ErrorKind.DeviceNotPaired, "not found after enumeration");
            }

            return Task.CompletedTask;
        });

    private void OnReceiverStateChanged(object? sender, ReceiverStateChangedEventArgs e)
    {
        if (e.State == ReceiverState.Closed)
        {
            RecordClosed();
        }

        _ = Enqueue(() => HandleReceiverStateAsync(e.DeviceId, e.State));
    }

    private void RecordClosed() => Interlocked.Exchange(ref _lastClosedAtTicks, _clock.UtcNow.UtcTicks);

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
        else if (_snapshot is { State: ConnectionState.Error, GaveUp: true })
        {
            // The phone we gave up on came back (re-paired, or Bluetooth cycled): try again with a fresh policy.
            _logger.LogInformation("Phone {Device} reappeared; connecting again", LogRedaction.DeviceId(device.Id));
            StopSlowRetry();
            _isReconnecting = true;
            await BeginConnectAsync(isReconnect: true, CancellationToken.None).ConfigureAwait(false);
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

    private async Task HandleReceiverStateAsync(string deviceId, ReceiverState receiverState)
    {
        var state = _snapshot.State;
        if (!SameDevice(deviceId, _targetDeviceId))
        {
            _logger.LogDebug("Receiver {ReceiverState} for non-active device ignored", receiverState);
            return;
        }

        if (receiverState == ReceiverState.Opened && state == ConnectionState.Opening && _reopenPending)
        {
            _logger.LogDebug("Receiver Opened ignored: closed on purpose, waiting to reopen");
        }
        else if (receiverState == ReceiverState.Opened && state == ConnectionState.Opening)
        {
            await OnOpenedAsync().ConfigureAwait(false);
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
            if (snapshot.Error is ErrorKind.BluetoothOff or ErrorKind.NoAdapter)
            {
                // E.g. the adapter came back (NoAdapter) but is switched off: show what is true now; the resume flag stays.
                if (snapshot.State == ConnectionState.Error && snapshot.Error != radioError && _targetDeviceId is not null)
                {
                    SetState(ConnectionState.Error, radioError);
                }

                return;
            }

            if (_targetDeviceId is null
                || (snapshot.State == ConnectionState.Disconnected && !_isReconnecting))
            {
                return;
            }

            // Collapses Closed + radio Off + Removed (all within ~140 ms) into this single transition.
            var resume = snapshot.State != ConnectionState.Connected || AutoReconnect;
            CancelLinkLoss();
            StopReconnecting();
            StopSlowRetry();
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
            var wasOpen = _receiver.State == ReceiverState.Opened || _snapshot.State == ConnectionState.Connected;
            await AwaitFlushingEventsAsync(_receiver.CloseAsync()).ConfigureAwait(false);
            if (wasOpen)
            {
                RecordClosed();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the receiver failed");
        }
    }

    /// <summary>
    /// Awaits a platform call made under the transition lock. If it hasn't completed yet (it may take seconds:
    /// OpenAsync, the open timeout, CloseAsync), queued events are raised first on a pool thread that doesn't hold
    /// the lock, so a UI sees Enabling/Opening while the call runs and handlers may still block on
    /// <see cref="DisconnectAsync"/> without deadlocking.
    /// </summary>
    private async Task<T> AwaitFlushingEventsAsync<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            FlushEventsInBackground();
        }

        return await task.ConfigureAwait(false);
    }

    private async Task AwaitFlushingEventsAsync(Task task)
    {
        if (!task.IsCompleted)
        {
            FlushEventsInBackground();
        }

        await task.ConfigureAwait(false);
    }

    private void FlushEventsInBackground()
    {
        lock (_sync)
        {
            if (_dispatchingEvents || _pendingEvents.Count == 0)
            {
                return;
            }
        }

        // Tracked like a transition so WhenIdleAsync also waits for these events to be delivered.
        var flush = Task.Run(DispatchPendingEvents);
        lock (_sync)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            if (!flush.IsCompleted)
            {
                _inFlight.Add(flush);
            }
        }
    }

    private void SetState(ConnectionState state, ErrorKind? error, bool gaveUp = false)
    {
        var device = state == ConnectionState.Idle || _targetDevice is null
            ? null
            : _targetDevice with { IsConnected = state == ConnectionState.Connected };
        gaveUp &= state == ConnectionState.Error;
        if (state != ConnectionState.Opening)
        {
            _primingActive = false;
        }

        var previous = _snapshot;
        if (previous.State == state && previous.Error == error && previous.Device == device && previous.GaveUp == gaveUp)
        {
            return;
        }

        _snapshot = new Snapshot(state, error, device, gaveUp);
        _logger.LogInformation(
            "State {Previous} -> {State}{Error}{GaveUp} ({Device})",
            previous.State,
            state,
            error is null ? string.Empty : $"({error})",
            gaveUp ? " [gave up]" : string.Empty,
            LogRedaction.DeviceId(device?.Id));

        // Raised by DispatchPendingEvents after the transition lock is released.
        lock (_sync)
        {
            _pendingEvents.Enqueue(new ConnectionStateChangedEventArgs(previous.State, state, error, device));
        }
    }

    private sealed record Snapshot(ConnectionState State, ErrorKind? Error, PhoneDevice? Device, bool GaveUp);
}

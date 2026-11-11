using DualAudio.Core.Abstractions;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Errors;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Audio;

namespace DualAudio.Platform.Windows.Bluetooth;

/// <summary>
/// Holds at most one <see cref="AudioPlaybackConnection"/>. Ported from the Phase 0 probe.
/// <list type="bullet">
/// <item>Opened/Closed are reported from the connection's StateChanged event (State can read Closed right
/// after a successful OpenAsync).</item>
/// <item>The timeout comes from the caller's token. A cancelled/timed-out attempt is unsubscribed and disposed
/// at once; if its StartAsync/OpenAsync completes later, it is never reported as connected.</item>
/// <item>Expected failures return <see cref="OpenResult.Failed"/>; cancellation throws <see cref="OperationCanceledException"/>.</item>
/// </list>
/// </summary>
public sealed class PlaybackConnectionReceiver : IAudioReceiver
{
    private readonly ILogger _logger;

    // Serializes OpenAsync/CloseAsync. Never disposed: a late waiter must not hit ObjectDisposedException.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cancelled by Dispose. Never disposed, so its Token stays readable by in-flight attempts.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private ConnectionHandle? _current; // guarded by _sync
    private int _disposed;

    public PlaybackConnectionReceiver(ILogger<PlaybackConnectionReceiver>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<ReceiverStateChangedEventArgs>? StateChanged;

    public ReceiverState State
    {
        get
        {
            ConnectionHandle? current;
            lock (_sync)
            {
                current = _current;
            }

            return current?.ReadState(_logger) ?? ReceiverState.Closed;
        }
    }

    public async Task<OpenResult> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ReleaseCurrent("closed by caller");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Aborts an in-flight attempt (it disposes its own connection) and releases the current one.
        _lifetime.Cancel();
        ReleaseCurrent("receiver disposed");
    }

    private async Task<OpenResult> OpenCoreAsync(string deviceId, CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();

        var device = LogRedaction.DeviceId(deviceId);
        ReleaseCurrent("replaced by a new connection");

        AudioPlaybackConnection? connection;
        try
        {
            connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
        }
        catch (Exception ex)
        {
            var failed = OpenResultMapping.FromException("TryCreateFromId", ex);
            _logger.LogWarning("TryCreateFromId({Device}) failed: {Kind} ({Detail})", device, failed.ErrorKind, failed.Detail);
            return failed;
        }

        if (connection is null)
        {
            var kind = await ClassifyMissingDeviceAsync(deviceId, token).ConfigureAwait(false);
            _logger.LogWarning("TryCreateFromId({Device}) returned null -> {Kind}", device, kind);
            return OpenResult.Failed(kind, "TryCreateFromId returned null");
        }

        var handle = new ConnectionHandle(this, connection, deviceId);
        handle.Attach();
        lock (_sync)
        {
            _current = handle;
        }

        var step = "StartAsync";
        Task? pending = null;
        IAsyncInfo? operation = null;
        try
        {
            var start = connection.StartAsync();
            operation = start;
            pending = start.AsTask();
            await pending.WaitAsync(token).ConfigureAwait(false);

            step = "OpenAsync";
            var open = connection.OpenAsync();
            operation = open;
            var openTask = open.AsTask();
            pending = openTask;
            var result = await openTask.WaitAsync(token).ConfigureAwait(false);

            var mapped = OpenResultMapping.FromStatus(result.Status, result.ExtendedError?.HResult ?? 0);
            if (mapped.Success)
            {
                // Don't report Connected here: the coordinator waits for StateChanged -> Opened.
                _logger.LogInformation("OpenAsync({Device}) succeeded; State={State}", device, handle.ReadState(_logger));
                return mapped;
            }

            _logger.LogWarning("OpenAsync({Device}) failed: {Kind} ({Detail})", device, mapped.ErrorKind, mapped.Detail);
            Release(handle, "open failed");
            return mapped;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var why = callerToken.IsCancellationRequested ? "cancelled or timed out by caller" : "receiver disposed";
            _logger.LogWarning("{Step}({Device}) abandoned: {Why}", step, device, why);
            Release(handle, "attempt abandoned");
            TryCancel(operation);
            DisposeOnLateCompletion(pending, handle, step, device);
            throw new OperationCanceledException($"{step} abandoned: {why}", null, token);
        }
        catch (Exception ex)
        {
            var failed = OpenResultMapping.FromException(step, ex);
            _logger.LogWarning("{Step}({Device}) threw: {Kind} ({Detail})", step, device, failed.ErrorKind, failed.Detail);
            Release(handle, step + " threw");
            return failed;
        }
    }

    /// <summary>
    /// TryCreateFromId returned null. If the device is still listed as an A2DP source it is most likely
    /// out of range or its Bluetooth is off (DeviceNotFound, retryable). If it exists but is not an A2DP
    /// source, the sink isn't supported for it. Otherwise it's gone.
    /// </summary>
    private async Task<ErrorKind> ClassifyMissingDeviceAsync(string deviceId, CancellationToken token)
    {
        try
        {
            var sources = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector())
                .AsTask(token).ConfigureAwait(false);
            if (sources.Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                return ErrorKind.DeviceNotFound;
            }

            var info = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(token).ConfigureAwait(false);
            return info is null ? ErrorKind.DeviceNotFound : ErrorKind.SinkNotSupported;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Classifying a missing device threw HRESULT={HResult}", SafeEvent.HResult(ex.HResult));
            return ErrorKind.DeviceNotFound;
        }
    }

    private void TryCancel(IAsyncInfo? operation)
    {
        if (operation is null)
        {
            return;
        }

        try
        {
            if (operation.Status == AsyncStatus.Started)
            {
                operation.Cancel();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cancelling the WinRT operation threw HRESULT={HResult}", SafeEvent.HResult(ex.HResult));
        }
    }

    /// <summary>If the abandoned StartAsync/OpenAsync completes later, make sure that connection stays disposed.</summary>
    private void DisposeOnLateCompletion(Task? pending, ConnectionHandle handle, string step, string device)
    {
        if (pending is null)
        {
            return;
        }

        _ = pending.ContinueWith(
            t =>
            {
                try
                {
                    _logger.LogInformation(
                        "Abandoned {Step}({Device}) finished late as {Status}; connection stays disposed",
                        step,
                        device,
                        t.Status);
                    _ = t.Exception; // observe
                    handle.Release(_logger, "late completion of an abandoned attempt");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Late-completion cleanup failed");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseCurrent(string reason)
    {
        ConnectionHandle? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }

        current?.Release(_logger, reason);
    }

    private void Release(ConnectionHandle handle, string reason)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, handle))
            {
                _current = null;
            }
        }

        handle.Release(_logger, reason);
    }

    private void OnConnectionStateChanged(ConnectionHandle handle)
    {
        try
        {
            if (handle.IsReleased)
            {
                return;
            }

            var state = handle.ReadState(_logger);
            _logger.LogInformation("Connection {Device} StateChanged -> {State}", LogRedaction.DeviceId(handle.DeviceId), state);
            SafeEvent.Raise(StateChanged, this, new ReceiverStateChangedEventArgs(handle.DeviceId, state), _logger, nameof(StateChanged));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioPlaybackConnection.StateChanged handler failed");
        }
    }

    /// <summary>One connection object plus its StateChanged subscription. Release is idempotent.</summary>
    private sealed class ConnectionHandle
    {
        private readonly AudioPlaybackConnection _connection;
        private readonly TypedEventHandler<AudioPlaybackConnection, object> _stateChanged;
        private int _released;

        public ConnectionHandle(PlaybackConnectionReceiver owner, AudioPlaybackConnection connection, string deviceId)
        {
            _connection = connection;
            DeviceId = deviceId;
            _stateChanged = (_, _) => owner.OnConnectionStateChanged(this);
        }

        public string DeviceId { get; }

        public bool IsReleased => Volatile.Read(ref _released) == 1;

        public void Attach() => _connection.StateChanged += _stateChanged;

        public ReceiverState ReadState(ILogger logger)
        {
            try
            {
                return _connection.State == AudioPlaybackConnectionState.Opened ? ReceiverState.Opened : ReceiverState.Closed;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Reading AudioPlaybackConnection.State threw HRESULT={HResult}", SafeEvent.HResult(ex.HResult));
                return ReceiverState.Closed;
            }
        }

        /// <summary>Unsubscribe, then Dispose (audio stops). Runs once.</summary>
        public void Release(ILogger logger, string reason)
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            try
            {
                _connection.StateChanged -= _stateChanged;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unsubscribing AudioPlaybackConnection.StateChanged failed");
            }

            try
            {
                _connection.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AudioPlaybackConnection.Dispose failed");
            }

            logger.LogInformation("Connection {Device} disposed ({Reason})", LogRedaction.DeviceId(DeviceId), reason);
        }
    }
}

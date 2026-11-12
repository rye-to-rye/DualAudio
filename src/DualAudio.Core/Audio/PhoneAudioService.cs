using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Audio;

public sealed record PhoneAudioOptions
{
    /// <summary>Minimum allowed <see cref="PollInterval"/>.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the peak meter is read while Connected. Clamped to at least 250 ms.</summary>
    public TimeSpan PollInterval { get; init; } = MinPollInterval;

    /// <summary>Default <see cref="ReceivingHoldTime"/>: long enough to bridge the gap between short videos.</summary>
    public static readonly TimeSpan DefaultReceivingHoldTime = TimeSpan.FromSeconds(10);

    /// <summary>Default <see cref="PeakThreshold"/>.</summary>
    public const float DefaultPeakThreshold = 0.001f;

    /// <summary>
    /// Hysteresis: IsReceivingAudio goes true on the first peak above <see cref="PeakThreshold"/> on an Active
    /// session and goes false only after this long with no such peak.
    /// </summary>
    public TimeSpan ReceivingHoldTime { get; init; } = DefaultReceivingHoldTime;

    /// <summary>Peak level (0..1) that counts as sound. Phase 0: ~0.127 while playing, 0.000 when quiet.</summary>
    public float PeakThreshold { get; init; } = DefaultPeakThreshold;
}

/// <summary>What the UI shows about phone audio.</summary>
/// <param name="VolumePercent">The user's phone volume (0..100), persisted in AppSettings.</param>
/// <param name="Muted">The user's phone mute, persisted in AppSettings.</param>
/// <param name="IsReceivingAudio">Connected AND the default endpoint's phone session was Active with a peak above
/// the threshold within the hold time. While <paramref name="Muted"/> it keeps its value from before the mute
/// (Windows reports a muted session Inactive). Independent of Connected: a paused phone is Connected but not receiving.</param>
/// <param name="IsAvailable">At least one phone session exists, so volume/mute have an effect.</param>
public sealed record PhoneAudioSnapshot(int VolumePercent, bool Muted, bool IsReceivingAudio, bool IsAvailable);

public sealed class PhoneAudioChangedEventArgs(PhoneAudioSnapshot previous, PhoneAudioSnapshot current) : EventArgs
{
    public PhoneAudioSnapshot Previous { get; } = previous;

    public PhoneAudioSnapshot Current { get; } = current;
}

/// <summary>
/// Phone volume/mute (persisted, applied through <see cref="IAudioSessionController"/>, re-applied on every
/// Connected) and "receiving audio" detection: while Connected, polls the controller's status every
/// <see cref="PhoneAudioOptions.PollInterval"/> (never faster than 250 ms, never while not Connected).
/// <para>Same event rules as <see cref="ConnectionCoordinator.StateChanged"/>: state changes are decided under one
/// <see cref="SemaphoreSlim"/>, <see cref="Changed"/> is raised after it is released, in order, on a background
/// thread, so handlers may call back into this service. Handlers marshal to the UI thread themselves.</para>
/// </summary>
public sealed class PhoneAudioService : IAsyncDisposable
{
    private readonly IAudioSessionController _sessions;
    private readonly ConnectionCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly PhoneAudioOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Guarded by _sync.
    private readonly object _sync = new();
    private readonly List<Task> _inFlight = [];
    private readonly Queue<PhoneAudioChangedEventArgs> _pendingEvents = new();
    private bool _dispatchingEvents;

    private volatile PhoneAudioSnapshot _snapshot = new(100, false, false, false);
    private int _disposed;

    // Only touched while holding _lock.
    private bool _initialized;
    private CancellationTokenSource? _pollCts;
    private DateTimeOffset? _lastHeard;

    public PhoneAudioService(
        IAudioSessionController sessions,
        ConnectionCoordinator coordinator,
        ISettingsStore settings,
        IClock clock,
        PhoneAudioOptions? options = null,
        ILogger<PhoneAudioService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);

        _sessions = sessions;
        _coordinator = coordinator;
        _settings = settings;
        _clock = clock;
        var o = options ?? new PhoneAudioOptions();
        _options = o.PollInterval < PhoneAudioOptions.MinPollInterval ? o with { PollInterval = PhoneAudioOptions.MinPollInterval } : o;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        _coordinator.StateChanged += OnConnectionStateChanged;
        _sessions.PhoneSessionsChanged += OnPhoneSessionsChanged;
    }

    public event EventHandler<PhoneAudioChangedEventArgs>? Changed;

    public PhoneAudioSnapshot Current => _snapshot;

    public int VolumePercent => _snapshot.VolumePercent;

    public bool Muted => _snapshot.Muted;

    public bool IsReceivingAudio => _snapshot.IsReceivingAudio;

    public bool IsAvailable => _snapshot.IsAvailable;

    /// <summary>The effective poll interval (after clamping).</summary>
    public TimeSpan PollInterval => _options.PollInterval;

    /// <summary>
    /// Loads PhoneVolume/PhoneMuted from settings and applies them to every phone session. Until this runs,
    /// nothing is applied (so the service never forces defaults over what the user set elsewhere).
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Enqueue(async () =>
        {
            var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            Update(_snapshot with { VolumePercent = settings.PhoneVolume, Muted = settings.PhoneMuted });
            _logger.LogInformation("Phone audio settings loaded: volume {Volume}%, muted {Muted}", settings.PhoneVolume, settings.PhoneMuted);
            await ApplyAsync(cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (_coordinator.CurrentState == ConnectionState.Connected)
            {
                StartPolling();
            }
        });

    /// <summary>Sets and persists the phone volume (0..100, clamped). Works while disconnected: applied on connect.</summary>
    public Task SetVolumeAsync(int volumePercent, CancellationToken cancellationToken = default)
    {
        var volume = Math.Clamp(volumePercent, 0, 100);
        return Enqueue(async () =>
        {
            _initialized = true;
            Update(_snapshot with { VolumePercent = volume });
            await CallAsync(ct => _sessions.SetPhoneVolumeAsync(volume, ct), "set volume", cancellationToken).ConfigureAwait(false);
            await SaveSettingsAsync(s => s with { PhoneVolume = volume }, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>Sets and persists the phone mute. Works while disconnected: applied on connect.</summary>
    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        Enqueue(async () =>
        {
            _initialized = true;
            Update(_snapshot with { Muted = muted });
            await CallAsync(ct => _sessions.SetPhoneMutedAsync(muted, ct), "set mute", cancellationToken).ConfigureAwait(false);
            await SaveSettingsAsync(s => s with { PhoneMuted = muted }, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _coordinator.StateChanged -= OnConnectionStateChanged;
        _sessions.PhoneSessionsChanged -= OnPhoneSessionsChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            StopPolling();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Test hook: completes when no queued or running operation remains.</summary>
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

    // ---------------------------------------------------------------- events in

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        _ = Enqueue(async () =>
        {
            if (e.Current == ConnectionState.Connected)
            {
                // Reconnect may have produced fresh sessions; Phase 0 left "survives reconnect" untested.
                if (_initialized)
                {
                    await ApplyAsync(_lifetime.Token).ConfigureAwait(false);
                }

                StartPolling();
                await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            }
            else if (e.Previous == ConnectionState.Connected || _pollCts is not null)
            {
                StopPolling();
                await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            }
        });

    private void OnPhoneSessionsChanged(object? sender, EventArgs e) =>
        _ = Enqueue(() => RefreshAsync(_lifetime.Token));

    // ---------------------------------------------------------------- polling

    private void StartPolling()
    {
        if (_pollCts is not null)
        {
            return;
        }

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lastHeard = null;
        _logger.LogDebug("Peak polling started ({Interval} ms)", _options.PollInterval.TotalMilliseconds);
        _ = PollLoopAsync(_pollCts.Token);
    }

    private void StopPolling()
    {
        if (_pollCts is null)
        {
            return;
        }

        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
        _lastHeard = null;
        _logger.LogDebug("Peak polling stopped");
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(_options.PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Enqueue(() => token.IsCancellationRequested ? Task.CompletedTask : RefreshAsync(token)).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- work (under _lock)

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        await CallAsync(ct => _sessions.SetPhoneVolumeAsync(snapshot.VolumePercent, ct), "apply volume", cancellationToken).ConfigureAwait(false);
        await CallAsync(ct => _sessions.SetPhoneMutedAsync(snapshot.Muted, ct), "apply mute", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the controller status; updates IsAvailable always and IsReceivingAudio only while polling.</summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        PhoneAudioStatus status;
        try
        {
            status = await _sessions.GetPhoneAudioStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading phone audio status failed");
            status = PhoneAudioStatus.Unavailable;
        }

        var receiving = false;
        if (_pollCts is not null)
        {
            var now = _clock.UtcNow;
            if (_snapshot.Muted)
            {
                // Hardware: muting makes Windows report the phone session Inactive although the peak stays > 0,
                // so the mute itself must not flip this. Keep the pre-mute value, and keep the hold window fresh
                // so unmuting starts a full hold instead of dropping to false at once.
                receiving = _snapshot.IsReceivingAudio;
                if (receiving)
                {
                    _lastHeard = now;
                }
            }
            else
            {
                if (status.IsActiveOnDefault && status.Peak > _options.PeakThreshold)
                {
                    _lastHeard = now;
                }

                // Hysteresis: true on the first qualifying peak, false only after the hold time without one.
                receiving = _lastHeard is { } heard && now - heard < _options.ReceivingHoldTime;
            }
        }

        Update(_snapshot with { IsReceivingAudio = receiving, IsAvailable = status.Available });
    }

    private async Task CallAsync(Func<CancellationToken, Task> call, string what, CancellationToken cancellationToken)
    {
        try
        {
            await call(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phone audio: {What} failed", what);
        }
    }

    private async Task<Settings.AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Sanitize();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings failed; using defaults");
            return Settings.AppSettings.Default;
        }
    }

    /// <summary>Re-reads settings before writing so fields owned by other components are not overwritten.</summary>
    private async Task SaveSettingsAsync(Func<Settings.AppSettings, Settings.AppSettings> change, CancellationToken cancellationToken)
    {
        try
        {
            var current = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await _settings.SaveAsync(change(current), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving phone audio settings failed");
        }
    }

    private void Update(PhoneAudioSnapshot next)
    {
        var previous = _snapshot;
        if (previous == next)
        {
            return;
        }

        _snapshot = next;
        if (previous.IsReceivingAudio != next.IsReceivingAudio)
        {
            _logger.LogInformation("IsReceivingAudio -> {Receiving}", next.IsReceivingAudio);
        }

        lock (_sync)
        {
            _pendingEvents.Enqueue(new PhoneAudioChangedEventArgs(previous, next));
        }
    }

    // ---------------------------------------------------------------- serialization (same pattern as ConnectionCoordinator)

    private Task Enqueue(Func<Task> operation)
    {
        var task = RunSerializedAsync(operation);
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

    private async Task RunSerializedAsync(Func<Task> operation)
    {
        try
        {
            await _lock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            // Caller or lifetime cancelled; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure in phone audio service");
        }
        finally
        {
            _lock.Release();
            DispatchPendingEvents();
        }
    }

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
            PhoneAudioChangedEventArgs args;
            lock (_sync)
            {
                if (!_pendingEvents.TryDequeue(out args!))
                {
                    _dispatchingEvents = false;
                    return;
                }
            }

            var handler = Changed;
            if (handler is null)
            {
                continue;
            }

            foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<PhoneAudioChangedEventArgs>>())
            {
                try
                {
                    subscriber(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PhoneAudio Changed subscriber threw");
                }
            }
        }
    }
}

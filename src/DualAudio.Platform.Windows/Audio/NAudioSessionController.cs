using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// Phone-session volume/mute over WASAPI sessions (docs/phase0-findings.md): Windows keeps ONE phone session per
/// render endpoint, Active only on the default one, each with its own volume. So volume/mute go to EVERY matching
/// session (<see cref="PhoneSessionMatcher"/>) on EVERY active render endpoint, and are re-applied when a session is
/// created, the default endpoint changes, or endpoints are added. Non-phone sessions are never written to.
/// <para>All COM work runs on one dedicated MTA thread; Core Audio callbacks only post work to it. Nothing touches
/// COM until the first call. The requested volume/mute is remembered even when no session exists yet.</para>
/// <para>Enforcement only writes when a session's value differs, and a change made in the Windows volume mixer is
/// overwritten by the next re-apply (the app's value wins).</para>
/// </summary>
public sealed class NAudioSessionController : IAudioSessionController
{
    private readonly ICoreAudioBackend _backend;
    private readonly MtaWorker _worker;
    private readonly ILogger _logger;
    private int _disposed;

    // Worker-thread only.
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultEndpointId;
    private int? _volumePercent;
    private bool? _muted;
    private bool _started;
    private DefaultSignature _signature = new(false, false);

    public NAudioSessionController(ILogger<NAudioSessionController>? logger = null)
        : this(null, logger)
    {
    }

    internal NAudioSessionController(ICoreAudioBackend? backend, ILogger? logger)
    {
        _logger = logger ?? NullLogger.Instance;
        _backend = backend ?? new NAudioCoreAudioBackend(_logger);
        _worker = new MtaWorker("DualAudio phone sessions", _logger);
        _backend.SessionCreated += OnBackendSessionCreated;
        _backend.EndpointsChanged += OnBackendEndpointsChanged;
        _backend.DefaultEndpointChanged += OnBackendDefaultChanged;
    }

    public event EventHandler? PhoneSessionsChanged;

    public Task<IReadOnlyList<PhoneSessionInfo>> GetPhoneSessionsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<PhoneSessionInfo>>(
            () =>
            {
                Rescan("query");
                return [.. _tracked.Values.Select(t => t.Handle.Info)];
            },
            [],
            cancellationToken);

    public Task SetPhoneVolumeAsync(int volumePercent, CancellationToken cancellationToken) =>
        RunAsync(
            () =>
            {
                _volumePercent = Math.Clamp(volumePercent, 0, 100);
                Rescan("set volume"); // fresh handles: a reconnect may have replaced the session objects
                return Enforce();
            },
            0,
            cancellationToken);

    public Task SetPhoneMutedAsync(bool muted, CancellationToken cancellationToken) =>
        RunAsync(
            () =>
            {
                _muted = muted;
                Rescan("set mute");
                return Enforce();
            },
            0,
            cancellationToken);

    public Task<bool> IsReceivingAudioAsync(CancellationToken cancellationToken) =>
        RunAsync(() => ReadStatus().IsActiveOnDefault, false, cancellationToken);

    public Task<PhoneAudioStatus> GetPhoneAudioStatusAsync(CancellationToken cancellationToken) =>
        RunAsync(ReadStatus, PhoneAudioStatus.Unavailable, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _backend.SessionCreated -= OnBackendSessionCreated;
        _backend.EndpointsChanged -= OnBackendEndpointsChanged;
        _backend.DefaultEndpointChanged -= OnBackendDefaultChanged;

        try
        {
            _worker.InvokeAsync(Cleanup).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Releasing audio sessions failed");
        }

        _worker.Dispose();
    }

    /// <summary>Test hook: completes after everything posted so far has run.</summary>
    internal Task FlushAsync() => _worker.InvokeAsync(() => { });

    // ---------------------------------------------------------------- callbacks (any thread → worker)

    private void OnBackendSessionCreated(object? sender, string endpointId) => Post("session created");

    private void OnBackendEndpointsChanged(object? sender, EventArgs e) => Post("endpoints changed");

    private void OnBackendDefaultChanged(object? sender, EventArgs e) => Post("default endpoint changed");

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        _worker.Post(() => Guard(() =>
        {
            if (sender is IAudioSessionHandle handle && SafeRead(() => handle.IsExpired, true))
            {
                Rescan("session expired");
                Enforce();
            }

            UpdateSignature();
        }));

    private void Post(string reason) =>
        _worker.Post(() => Guard(() =>
        {
            if (!_started)
            {
                return; // nothing to re-apply yet; the first call scans everything
            }

            Rescan(reason);
            Enforce();
        }));

    // ---------------------------------------------------------------- worker-thread logic

    private Task<T> RunAsync<T>(Func<T> work, T fallback, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return Task.FromResult(fallback);
        }

        return RunCoreAsync();

        async Task<T> RunCoreAsync()
        {
            try
            {
                return await _worker.InvokeAsync(
                    () =>
                    {
                        if (!EnsureStarted())
                        {
                            return fallback;
                        }

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
                // Never throw audio-stack failures into the caller (UI).
                _logger.LogWarning(ex, "Audio session operation failed");
                return fallback;
            }
        }
    }

    private bool EnsureStarted()
    {
        if (_started)
        {
            return true;
        }

        try
        {
            _backend.Start();
            _started = true;
            Rescan("start");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Core Audio is unavailable; will retry on the next call");
            return false;
        }
    }

    /// <summary>Re-enumerates every active render endpoint, replacing all tracked handles with fresh ones.</summary>
    private void Rescan(string reason)
    {
        _defaultEndpointId = SafeRead(_backend.GetDefaultRenderEndpointId, null);
        var endpoints = SafeRead(_backend.GetActiveRenderEndpointIds, []);
        var fresh = new Dictionary<string, Tracked>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpointId in endpoints)
        {
            IReadOnlyList<IAudioSessionHandle> sessions;
            try
            {
                sessions = _backend.GetSessions(endpointId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Enumerating sessions on an endpoint failed");
                continue;
            }

            foreach (var session in sessions)
            {
                if (!IsPhone(session))
                {
                    session.Dispose();
                    continue;
                }

                var key = endpointId + "|" + (session.Info.SessionInstanceIdentifier ?? session.Info.SessionIdentifier ?? string.Empty);
                if (fresh.ContainsKey(key))
                {
                    session.Dispose();
                    continue;
                }

                try
                {
                    session.StateChanged += OnSessionStateChanged;
                    session.EnableNotifications();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Registering for session notifications failed");
                }

                fresh[key] = new Tracked(endpointId, session);
            }
        }

        var appeared = fresh.Keys.Count(k => !_tracked.ContainsKey(k));
        var disappeared = _tracked.Keys.Count(k => !fresh.ContainsKey(k));
        foreach (var old in _tracked.Values)
        {
            Release(old);
        }

        _tracked.Clear();
        foreach (var (key, tracked) in fresh)
        {
            _tracked[key] = tracked;
        }

        if (appeared > 0 || disappeared > 0)
        {
            _logger.LogInformation(
                "Phone sessions ({Reason}): {Count} on {Endpoints} endpoint(s), +{Appeared} -{Disappeared}; default endpoint has one: {OnDefault}",
                reason,
                _tracked.Count,
                endpoints.Count,
                appeared,
                disappeared,
                _tracked.Values.Any(t => IsDefault(t.EndpointId)));
        }

        UpdateSignature();
    }

    /// <summary>Writes the requested volume/mute to every tracked phone session whose value differs.</summary>
    private int Enforce()
    {
        if (_volumePercent is null && _muted is null)
        {
            return 0;
        }

        var writes = 0;
        foreach (var tracked in _tracked.Values)
        {
            var handle = tracked.Handle;
            try
            {
                if (_volumePercent is { } percent)
                {
                    var target = percent / 100f;
                    if (Math.Abs(handle.Volume - target) > 0.001f)
                    {
                        handle.Volume = target;
                        writes++;
                    }
                }

                if (_muted is { } muted && handle.Mute != muted)
                {
                    handle.Mute = muted;
                    writes++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Applying volume/mute to a phone session failed (session gone?)");
            }
        }

        if (writes > 0)
        {
            _logger.LogInformation(
                "Applied phone volume {Volume}% / muted {Muted} to {Sessions} session(s)",
                _volumePercent?.ToString() ?? "-",
                _muted?.ToString() ?? "-",
                _tracked.Count);
        }

        return writes;
    }

    private PhoneAudioStatus ReadStatus()
    {
        var sessions = new List<PhoneSessionState>(_tracked.Count);
        var peak = 0f;
        var stale = false;
        foreach (var tracked in _tracked.Values)
        {
            var handle = tracked.Handle;
            try
            {
                var onDefault = IsDefault(tracked.EndpointId);
                var active = handle.IsActive;
                sessions.Add(new PhoneSessionState(
                    tracked.EndpointId,
                    onDefault,
                    active,
                    (int)Math.Round(handle.Volume * 100f),
                    handle.Mute));
                if (onDefault)
                {
                    peak = Math.Max(peak, handle.Peak);
                }
            }
            catch (Exception ex)
            {
                stale = true;
                _logger.LogDebug(ex, "Reading a phone session failed (session gone?)");
            }
        }

        if (stale)
        {
            Post("stale session");
        }

        return new PhoneAudioStatus(sessions, peak);
    }

    private void UpdateSignature()
    {
        var onDefault = _tracked.Values.Where(t => IsDefault(t.EndpointId)).ToList();
        var signature = new DefaultSignature(
            onDefault.Count > 0,
            onDefault.Any(t => SafeRead(() => t.Handle.IsActive, false)));
        if (signature == _signature)
        {
            return;
        }

        _logger.LogDebug(
            "Phone session on default endpoint: exists {Exists} -> {NowExists}, active {Active} -> {NowActive}",
            _signature.Exists,
            signature.Exists,
            _signature.Active,
            signature.Active);
        _signature = signature;

        // Off the worker, so a handler that calls back into this controller can't deadlock it.
        var handler = PhoneSessionsChanged;
        if (handler is not null)
        {
            _ = Task.Run(() => Raise(handler));
        }
    }

    private void Raise(EventHandler handler)
    {
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhoneSessionsChanged subscriber threw");
            }
        }
    }

    private bool IsPhone(IAudioSessionHandle session)
    {
        try
        {
            return PhoneSessionMatcher.IsPhoneSession(session.Info);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsDefault(string endpointId) =>
        _defaultEndpointId is not null && string.Equals(endpointId, _defaultEndpointId, StringComparison.OrdinalIgnoreCase);

    private void Release(Tracked tracked)
    {
        tracked.Handle.StateChanged -= OnSessionStateChanged;
        try
        {
            tracked.Handle.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing a session failed");
        }
    }

    private void Cleanup()
    {
        foreach (var tracked in _tracked.Values)
        {
            Release(tracked);
        }

        _tracked.Clear();
        try
        {
            _backend.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the Core Audio backend failed");
        }
    }

    private void Guard(Action action)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handling an audio notification failed");
        }
    }

    private T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Core Audio read failed");
            return fallback;
        }
    }

    private sealed record Tracked(string EndpointId, IAudioSessionHandle Handle);

    private readonly record struct DefaultSignature(bool Exists, bool Active);
}

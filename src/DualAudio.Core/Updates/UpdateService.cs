using System.Globalization;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Updates;

/// <summary>An update found by <see cref="IAppUpdater"/>. <paramref name="Handle"/> is the platform's own object.</summary>
public sealed record AvailableUpdate(string Version, object? Handle = null);

/// <summary>
/// The installer/updater (Platform: Velopack + GitHub Releases). Never applies anything on its own: only
/// <see cref="ApplyAfterExit"/> does, and only when asked.
/// </summary>
public interface IAppUpdater
{
    /// <summary>False when running from `dotnet run` / a build folder: no update checks then.</summary>
    bool IsInstalled { get; }

    /// <summary>The installed version, or null when not installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>An update downloaded earlier (e.g. before the last exit) and not applied yet.</summary>
    AvailableUpdate? PendingRestart { get; }

    /// <summary>Null when up to date. Throws for network/feed errors.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);

    Task DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, then applies (and restarts only if asked).</summary>
    void ApplyAfterExit(AvailableUpdate update, bool restart);
}

public enum UpdateState
{
    /// <summary>Not an installed copy (dotnet run): updates are off.</summary>
    NotInstalled,

    /// <summary>Installed; no check finished yet.</summary>
    Idle,
    Checking,
    Downloading,
    UpToDate,

    /// <summary>Downloaded; applied on restart or exit.</summary>
    Ready,

    /// <summary>Last check failed (offline, no release, …). Quiet: shown only as text in Settings.</summary>
    Failed,
}

/// <param name="IsBannerVisible">Ready and the user hasn't pressed "Later" in this session.</param>
public sealed record UpdateSnapshot(
    UpdateState State,
    string? ReadyVersion,
    DateTimeOffset? LastCheckAt,
    string? LastResult,
    bool IsBannerVisible);

public sealed record UpdateOptions
{
    /// <summary>First automatic check this long after start, so start-up (and a game launch) isn't slowed.</summary>
    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(12);
}

/// <summary>
/// In-app updates, user in control: checks 60 s after start and every 12 h, downloads in the background, then shows a
/// quiet "Update ready" banner. Never restarts on its own: "Restart now" restarts, otherwise the update is applied
/// when the app exits (<see cref="PrepareForExit"/>). "Later" hides the banner until the next start. Failures are
/// logged and shown only as a quiet line in Settings. Raises <see cref="Changed"/> on a background thread.
/// </summary>
public sealed class UpdateService : IAsyncDisposable
{
    public const string NotInstalledText = "Updates are checked in the installed version only.";
    public const string UpToDateText = "Up to date";
    public const string FailedText = "Couldn't check for updates. DualAudio will try again later.";

    private readonly IAppUpdater _updater;
    private readonly IClock _clock;
    private readonly UpdateOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();

    // Guarded by _sync.
    private UpdateState _state;
    private AvailableUpdate? _ready;
    private DateTimeOffset? _lastCheckAt;
    private string? _lastResult;
    private bool _dismissed;
    private bool _applyScheduled;
    private int _started;

    public UpdateService(IAppUpdater updater, IClock clock, UpdateOptions? options = null, ILogger<UpdateService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(clock);
        _updater = updater;
        _clock = clock;
        _options = options ?? new UpdateOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _state = SafeIsInstalled() ? UpdateState.Idle : UpdateState.NotInstalled;
        _lastResult = _state == UpdateState.NotInstalled ? NotInstalledText : null;
    }

    public event EventHandler? Changed;

    /// <summary>"Restart now" was accepted: the app must exit now (the updater restarts it).</summary>
    public event EventHandler? RestartRequested;

    public UpdateSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return new UpdateSnapshot(_state, _ready?.Version, _lastCheckAt, _lastResult, _state == UpdateState.Ready && !_dismissed);
            }
        }
    }

    /// <summary>Schedules the checks (first after 60 s, then every 12 h). Does nothing when not installed.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        if (Current.State == UpdateState.NotInstalled)
        {
            _logger.LogInformation("Not an installed copy: update checks are off");
            return;
        }

        if (SafePending() is { } pending)
        {
            // Downloaded before the last exit but not applied (e.g. Windows shut down): ready right away.
            _logger.LogInformation("Update {Version} was downloaded earlier and is ready", pending.Version);
            Set(() =>
            {
                _ready = pending;
                _state = UpdateState.Ready;
                _lastResult = ReadyText(pending.Version);
            });
        }

        _ = LoopAsync(_lifetime.Token);
    }

    /// <summary>Settings "Check for updates". Never throws; the result is in <see cref="Current"/>.</summary>
    public Task CheckNowAsync() => CheckAndDownloadAsync("manual", _lifetime.Token);

    /// <summary>"Later": hides the banner (and tray line) until the next start. The update still applies on exit.</summary>
    public void Later()
    {
        _logger.LogInformation("Update banner dismissed until next start");
        Set(() => _dismissed = true);
    }

    /// <summary>"Restart now": schedules apply + restart and asks the app to exit. False when nothing is ready.</summary>
    public bool RestartNow()
    {
        AvailableUpdate? ready;
        lock (_sync)
        {
            ready = _state == UpdateState.Ready ? _ready : null;
        }

        if (ready is null || !TryApply(ready, restart: true))
        {
            return false;
        }

        _logger.LogInformation("User chose to restart to install {Version}", ready.Version);
        RaiseSafe(RestartRequested);
        return true;
    }

    /// <summary>Called on exit: a ready update is applied after the process ends, without restarting it.</summary>
    public void PrepareForExit()
    {
        AvailableUpdate? ready;
        lock (_sync)
        {
            ready = _state == UpdateState.Ready && !_applyScheduled ? _ready : null;
        }

        if (ready is not null && TryApply(ready, restart: false))
        {
            _logger.LogInformation("Update {Version} will be installed after exit (no restart)", ready.Version);
        }
    }

    public async ValueTask DisposeAsync() => await _lifetime.CancelAsync().ConfigureAwait(false);

    internal static string ReadyText(string version) => $"Version {version} is ready: restart to install";

    private async Task LoopAsync(CancellationToken token)
    {
        var delay = _options.FirstCheckDelay;
        while (true)
        {
            try
            {
                await _clock.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await CheckAndDownloadAsync("scheduled", token).ConfigureAwait(false);
            delay = _options.CheckInterval;
        }
    }

    private async Task CheckAndDownloadAsync(string why, CancellationToken token)
    {
        if (Current.State == UpdateState.NotInstalled)
        {
            _logger.LogInformation("Update check ({Why}) skipped: not an installed copy", why);
            return;
        }

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var wasReady = Current.State == UpdateState.Ready;
            if (!wasReady)
            {
                Set(() => _state = UpdateState.Checking);
            }

            _logger.LogInformation("Checking for updates ({Why})", why);
            var found = await _updater.CheckAsync(token).ConfigureAwait(false);
            var now = _clock.UtcNow;
            if (found is null)
            {
                _logger.LogInformation("No update available");
                Set(() =>
                {
                    _lastCheckAt = now;
                    if (!wasReady)
                    {
                        _state = UpdateState.UpToDate;
                        _lastResult = UpToDateText;
                    }
                });
                return;
            }

            if (wasReady && string.Equals(Current.ReadyVersion, found.Version, StringComparison.OrdinalIgnoreCase))
            {
                Set(() => _lastCheckAt = now);
                return;
            }

            _logger.LogInformation("Update {Version} found; downloading in the background", found.Version);
            if (!wasReady)
            {
                Set(() => _state = UpdateState.Downloading);
            }

            await _updater.DownloadAsync(found, token).ConfigureAwait(false);
            _logger.LogInformation("Update {Version} downloaded and ready", found.Version);
            Set(() =>
            {
                _lastCheckAt = now;
                _ready = found;
                _state = UpdateState.Ready;
                _lastResult = ReadyText(found.Version);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Exiting.
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, no release yet, …: log it, never alarm the user.
            _logger.LogWarning(ex, "Update check failed ({Why})", why);
            var now = _clock.UtcNow;
            Set(() =>
            {
                _lastCheckAt = now;
                if (_state != UpdateState.Ready)
                {
                    _state = UpdateState.Failed;
                    _lastResult = FailedText;
                }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryApply(AvailableUpdate update, bool restart)
    {
        lock (_sync)
        {
            if (_applyScheduled)
            {
                return false;
            }

            _applyScheduled = true;
        }

        try
        {
            _updater.ApplyAfterExit(update, restart);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduling the update failed");
            lock (_sync)
            {
                _applyScheduled = false;
            }

            return false;
        }
    }

    private bool SafeIsInstalled()
    {
        try
        {
            return _updater.IsInstalled;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the install state failed; treating as not installed");
            return false;
        }
    }

    private AvailableUpdate? SafePending()
    {
        try
        {
            return _updater.PendingRestart;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading a pending update failed");
            return null;
        }
    }

    private void Set(Action change)
    {
        UpdateSnapshot before;
        UpdateSnapshot after;
        lock (_sync)
        {
            before = Current;
            change();
            after = Current;
        }

        if (before != after)
        {
            RaiseSafe(Changed);
        }
    }

    private void RaiseSafe(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update event subscriber threw");
            }
        }
    }

    /// <summary>"Last checked 14:03 · Up to date" for Settings > About (local time).</summary>
    public static string Describe(UpdateSnapshot snapshot, TimeZoneInfo? zone = null)
    {
        if (snapshot.State == UpdateState.NotInstalled)
        {
            return NotInstalledText;
        }

        var result = snapshot.State switch
        {
            UpdateState.Checking => "Checking…",
            UpdateState.Downloading => "Downloading update…",
            _ => snapshot.LastResult ?? "Not checked yet",
        };
        if (snapshot.LastCheckAt is not { } at)
        {
            return result;
        }

        var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
        return $"Last checked {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · {result}";
    }
}

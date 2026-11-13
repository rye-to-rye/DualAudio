using System.Globalization;
using System.Text;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Diagnostics;

public sealed record ConnectionDiagnosticsOptions
{
    /// <summary>Extra lines this long after each Connected (Opened).</summary>
    public IReadOnlyList<TimeSpan> AfterOpenedDelays { get; init; } = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    /// <summary>How many recent lines are kept in memory (for "Copy diagnostics").</summary>
    public int MaxRecentLines { get; init; } = 50;
}

/// <summary>
/// Writes ONE structured log line (prefix <c>STATE</c>) describing the connection and the phone audio session at the
/// moments that explain a "connected but no audio": every coordinator transition (Enabling = "connect", Connected =
/// "opened"), 3 s and 10 s after Opened, and every phone-session change. No polling: event-driven only.
/// <para>Never contains audio content, phone names or Bluetooth addresses (device ids are redacted to 4 hex chars).
/// Never throws; a failed read shows as "?" in the line.</para>
/// </summary>
public sealed class ConnectionDiagnostics : IAsyncDisposable
{
    public const string LinePrefix = "STATE";

    private readonly ConnectionCoordinator _coordinator;
    private readonly IAudioSessionController _sessions;
    private readonly IOutputDeviceService _outputs;
    private readonly IClock _clock;
    private readonly ConnectionDiagnosticsOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private readonly object _sync = new();
    private readonly Queue<string> _recent = new();
    private readonly List<Task> _inFlight = [];
    private CancellationTokenSource? _followUps;
    private int _disposed;

    public ConnectionDiagnostics(
        ConnectionCoordinator coordinator,
        IAudioSessionController sessions,
        IOutputDeviceService outputs,
        IClock clock,
        ConnectionDiagnosticsOptions? options = null,
        ILogger<ConnectionDiagnostics>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(clock);

        _coordinator = coordinator;
        _sessions = sessions;
        _outputs = outputs;
        _clock = clock;
        _options = options ?? new ConnectionDiagnosticsOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        _coordinator.StateChanged += OnStateChanged;
        _sessions.PhoneSessionsChanged += OnPhoneSessionsChanged;
    }

    /// <summary>The most recent structured lines (oldest first), each prefixed with its UTC time.</summary>
    public IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (_sync)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>Writes one line now (e.g. "startup"). Fire-and-forget; never throws.</summary>
    public void Capture(string trigger) => Track(CaptureAsync(trigger, null));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _coordinator.StateChanged -= OnStateChanged;
        _sessions.PhoneSessionsChanged -= OnPhoneSessionsChanged;
        lock (_sync)
        {
            _followUps?.Cancel();
            _followUps?.Dispose();
            _followUps = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>Test hook: completes when every queued line has been written.</summary>
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

    /// <summary>Builds the line. Pure, so the format is unit-tested.</summary>
    internal static string Format(
        string trigger,
        ConnectionState coordinatorState,
        ReceiverState? receiverState,
        PhoneAudioStatus? status,
        IReadOnlyList<AudioEndpoint>? endpoints,
        bool priming,
        bool staleBeforeOpen,
        bool automaticReopenUsed,
        TimeSpan? sinceClosed,
        string? deviceId)
    {
        string Name(string? endpointId) =>
            endpointId is null ? "-"
            : endpoints?.FirstOrDefault(e => string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase))?.Name ?? "?";

        var defaultName = endpoints is null ? "?" : endpoints.FirstOrDefault(e => e.IsDefault)?.Name ?? "-";
        var phone = status?.Sessions.FirstOrDefault(s => s.IsOnDefaultEndpoint)
                    ?? status?.Sessions.FirstOrDefault(s => s.IsActive)
                    ?? status?.Sessions.FirstOrDefault();

        var sb = new StringBuilder(LinePrefix).Append(' ').Append(trigger).Append(':');
        Append(sb, "coord", coordinatorState.ToString());
        Append(sb, "recv", receiverState?.ToString() ?? "?");
        Append(sb, "session", status is null ? "?" : phone is null ? "none" : phone.IsOnDefaultEndpoint ? "found" : "other-endpoint");
        Append(sb, "sessionEndpoint", Quote(phone is null ? "-" : Name(phone.EndpointId)));
        Append(sb, "sessionState", phone is null ? "-" : phone.IsActive ? "Active" : "Inactive");
        Append(sb, "peak", status is null ? "?" : status.Peak.ToString("0.000", CultureInfo.InvariantCulture));
        Append(sb, "sessions", status?.Sessions.Count.ToString(CultureInfo.InvariantCulture) ?? "?");
        Append(sb, "default", Quote(defaultName));
        Append(sb, "muted", status is null ? "?" : YesNo(status.Sessions.Any(s => s.Muted)));
        Append(sb, "priming", YesNo(priming));
        Append(sb, "staleBeforeOpen", YesNo(staleBeforeOpen));
        Append(sb, "autoReopenUsed", YesNo(automaticReopenUsed));
        Append(sb, "sinceClosed", sinceClosed is { } t ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "never");
        Append(sb, "device", LogRedaction.DeviceId(deviceId));
        return sb.ToString();

        static void Append(StringBuilder sb, string key, string value) => sb.Append(' ').Append(key).Append('=').Append(value);

        static string Quote(string value) => "\"" + value.Replace("\"", "'", StringComparison.Ordinal) + "\"";

        static string YesNo(bool value) => value ? "yes" : "no";
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var trigger = e.Current switch
        {
            ConnectionState.Enabling => "connect",
            ConnectionState.Connected => "opened",
            _ => $"{e.Previous}->{e.Current}",
        };
        Track(CaptureAsync(trigger, e.Current));

        if (e.Current == ConnectionState.Connected)
        {
            ScheduleFollowUps();
        }
    }

    private void OnPhoneSessionsChanged(object? sender, EventArgs e) => Track(CaptureAsync("session-changed", null));

    /// <summary>"opened+3s" and "opened+10s" for the latest Connected; a newer Connected replaces them.</summary>
    private void ScheduleFollowUps()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            _followUps?.Cancel();
            _followUps?.Dispose();
            _followUps = cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }

        var token = cts.Token;
        _ = RunAsync(); // not tracked: it waits on the clock; each line it writes is tracked

        async Task RunAsync()
        {
            var elapsed = TimeSpan.Zero;
            foreach (var at in _options.AfterOpenedDelays.Order())
            {
                try
                {
                    await _clock.Delay(at - elapsed, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                elapsed = at;
                var capture = CaptureAsync("opened+" + at.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s", null);
                Track(capture);
                await capture.ConfigureAwait(false);
            }
        }
    }

    private async Task CaptureAsync(string trigger, ConnectionState? stateAtEvent)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            var token = _lifetime.Token;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var state = stateAtEvent ?? _coordinator.CurrentState;
                var receiver = Safe(() => _coordinator.ReceiverState);
                var status = await SafeAsync(() => _sessions.GetPhoneAudioStatusAsync(token)).ConfigureAwait(false);
                var endpoints = await SafeAsync(() => _outputs.GetRenderEndpointsAsync(token)).ConfigureAwait(false);
                var now = _clock.UtcNow;
                var sinceClosed = _coordinator.LastClosedAt is { } closed ? now - closed : (TimeSpan?)null;

                var line = Format(
                    trigger,
                    state,
                    receiver,
                    status,
                    endpoints,
                    _coordinator.IsPriming,
                    _coordinator.LastOpenFoundStaleSession,
                    _coordinator.AutomaticReopenUsed,
                    sinceClosed,
                    _coordinator.ActiveDevice?.Id);

                _logger.LogInformation("{Line}", line);
                lock (_sync)
                {
                    _recent.Enqueue(now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z " + line);
                    while (_recent.Count > Math.Max(1, _options.MaxRecentLines))
                    {
                        _recent.Dequeue();
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing a diagnostics line failed");
        }
    }

    private static T? Safe<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<T?> SafeAsync<T>(Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnostics read failed");
            return null;
        }
    }

    private void Track(Task task)
    {
        lock (_sync)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
            if (!task.IsCompleted)
            {
                _inFlight.Add(task);
            }
        }
    }
}

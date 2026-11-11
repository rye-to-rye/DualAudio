using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Connection;

public sealed record ReconnectOptions
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts before giving up. A successful connection resets the count.</summary>
    public int MaxAttempts { get; init; } = 10;
}

/// <summary>
/// Exponential backoff for automatic reconnects: 2 s, 4 s, 8 s, 16 s, then 30 s (cap) per attempt.
/// One attempt is pending at a time; the owner calls <see cref="TrySchedule"/> again after a failed
/// attempt and <see cref="Cancel"/> on user Disconnect or after success. Thread-safe.
/// </summary>
public sealed class ReconnectPolicy : IDisposable
{
    private readonly IClock _clock;
    private readonly ReconnectOptions _options;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _pending;
    private int _attemptsMade;
    private DateTimeOffset? _nextAttemptAt;
    private bool _disposed;

    public ReconnectPolicy(IClock clock, ReconnectOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _options = options ?? new ReconnectOptions();
        _logger = logger ?? NullLogger.Instance;

        if (_options.InitialDelay <= TimeSpan.Zero || _options.MaxDelay < _options.InitialDelay)
        {
            throw new ArgumentException("InitialDelay must be positive and not exceed MaxDelay.", nameof(options));
        }

        if (_options.MaxAttempts < 1)
        {
            throw new ArgumentException("MaxAttempts must be at least 1.", nameof(options));
        }
    }

    public int AttemptsMade
    {
        get { lock (_sync) { return _attemptsMade; } }
    }

    public bool IsPending
    {
        get { lock (_sync) { return _pending is not null; } }
    }

    /// <summary>When the pending attempt will run, for "Reconnecting in N s" UI. Null when nothing is pending.</summary>
    public DateTimeOffset? NextAttemptAt
    {
        get { lock (_sync) { return _nextAttemptAt; } }
    }

    public bool IsExhausted
    {
        get { lock (_sync) { return _attemptsMade >= _options.MaxAttempts; } }
    }

    /// <summary>Delay before the given 1-based attempt: InitialDelay * 2^(n-1), capped at MaxDelay.</summary>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var ticks = Math.Min(_options.InitialDelay.Ticks * factor, _options.MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Schedules the next attempt after its backoff delay, replacing any pending one.
    /// Returns false (and schedules nothing) when <see cref="ReconnectOptions.MaxAttempts"/> is reached.
    /// </summary>
    public bool TrySchedule(Func<Task> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        CancellationTokenSource cts;
        TimeSpan delay;
        int number;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attemptsMade >= _options.MaxAttempts)
            {
                return false;
            }

            CancelPendingLocked();
            number = ++_attemptsMade;
            delay = GetDelay(number);
            cts = new CancellationTokenSource();
            _pending = cts;
            _nextAttemptAt = _clock.UtcNow + delay;
        }

        _logger.LogInformation("Reconnect attempt {Attempt}/{Max} in {Delay}", number, _options.MaxAttempts, delay);
        _ = RunAsync(delay, attempt, cts);
        return true;
    }

    /// <summary>Cancels any pending attempt and resets the attempt count.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            CancelPendingLocked();
            _attemptsMade = 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CancelPendingLocked();
            _disposed = true;
        }
    }

    private async Task RunAsync(TimeSpan delay, Func<Task> attempt, CancellationTokenSource cts)
    {
        try
        {
            await _clock.Delay(delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_pending, cts) || cts.IsCancellationRequested)
            {
                return;
            }

            _pending = null;
            _nextAttemptAt = null;
        }

        cts.Dispose();

        try
        {
            await attempt().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect attempt threw");
        }
    }

    private void CancelPendingLocked()
    {
        if (_pending is null)
        {
            return;
        }

        _pending.Cancel();
        _pending.Dispose();
        _pending = null;
        _nextAttemptAt = null;
    }
}

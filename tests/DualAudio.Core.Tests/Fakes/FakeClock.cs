using DualAudio.Core.Abstractions;

namespace DualAudio.Core.Tests.Fakes;

/// <summary>Manual clock: delays complete only when <see cref="Advance"/> passes their due time.</summary>
internal sealed class FakeClock : IClock
{
    private readonly object _sync = new();
    private readonly List<Timer> _timers = [];
    private readonly List<TimeSpan> _requested = [];
    private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get { lock (_sync) { return _now; } }
    }

    public IReadOnlyList<TimeSpan> RequestedDelays
    {
        get { lock (_sync) { return [.. _requested]; } }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Continuations run inline on Advance(), so queued work is registered before Advance returns.
        var timer = new Timer(new TaskCompletionSource());
        lock (_sync)
        {
            _requested.Add(delay);
            timer.Due = _now + delay;
            _timers.Add(timer);
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_sync)
                {
                    _timers.Remove(timer);
                }

                timer.Completion.TrySetCanceled(cancellationToken);
            });
        }

        return timer.Completion.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_sync)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now).OrderBy(t => t.Due)];
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        // .NET refuses to inline await continuations while a SynchronizationContext (xUnit installs one)
        // is current; clear it so timer continuations run synchronously and deterministically here.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            foreach (var timer in due)
            {
                timer.Completion.TrySetResult();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class Timer(TaskCompletionSource completion)
    {
        public TaskCompletionSource Completion { get; } = completion;

        public DateTimeOffset Due { get; set; }
    }
}

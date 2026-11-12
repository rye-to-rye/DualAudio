using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// One dedicated MTA background thread that runs every Core Audio COM call for its owner, in order.
/// Core Audio callbacks arrive on arbitrary system threads; owners only <see cref="Post"/> work from them and
/// never call COM there (the callback thread holds an audio-stack lock).
/// </summary>
internal sealed class MtaWorker : IDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ILogger _logger;

    public MtaWorker(string name, ILogger logger)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>Runs <paramref name="work"/> on the worker. Exceptions from <paramref name="work"/> fault the task.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            Complete(tcs, work);
            return tcs.Task;
        }

        if (!TryAdd(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    Complete(tcs, work);
                }
            }))
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(MtaWorker)));
        }

        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            work();
            return true;
        }, cancellationToken);

    /// <summary>Fire-and-forget; dropped silently after disposal. Exceptions are logged.</summary>
    public void Post(Action work) => TryAdd(work);

    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!IsCurrentThread && !_thread.Join(JoinTimeout))
        {
            _logger.LogWarning("{Worker} did not stop within {Timeout}", _thread.Name, JoinTimeout);
        }
    }

    private static void Complete<T>(TaskCompletionSource<T> tcs, Func<T> work)
    {
        try
        {
            tcs.TrySetResult(work());
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private bool TryAdd(Action work)
    {
        try
        {
            return _queue.TryAdd(work);
        }
        catch (InvalidOperationException)
        {
            return false; // CompleteAdding was called (ObjectDisposedException derives from this too).
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Worker} work item failed", _thread.Name);
            }
        }
    }
}

namespace DualAudio.App.Services;

/// <summary>
/// One DualAudio per user session: a named Mutex marks the running instance, and a named EventWaitHandle lets a
/// second launch ask it to show its window before exiting.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\DualAudio.SingleInstance.v1";
    private const string ActivateEventName = @"Local\DualAudio.Activate.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>Raised on a thread-pool thread when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Returns the guard if this is the first instance; otherwise signals the first one and returns null.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExistingInstance();
            return null;
        }

        return new SingleInstanceGuard(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName));
    }

    public void StartListening() =>
        _registration ??= ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

    /// <summary>Must run on the thread that called <see cref="TryAcquire"/> (mutex ownership is per thread).</summary>
    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the handle close below still releases it.
        }

        _mutex.Dispose();
    }

    private static void SignalExistingInstance()
    {
        // Create-or-open, so a first instance that hasn't created its event yet still receives the signal.
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        activate.Set();
    }
}

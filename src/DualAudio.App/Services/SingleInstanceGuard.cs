namespace DualAudio.App.Services;

/// <summary>
/// One DualAudio per user session: a named Mutex marks the running instance, and named EventWaitHandles let another
/// process ask it to show its window (second launch) or to exit cleanly (installer update/uninstall hooks).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\DualAudio.SingleInstance.v1";
    private const string ActivateEventName = @"Local\DualAudio.Activate.v1";
    private const string ExitEventName = @"Local\DualAudio.Exit.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _exit;
    private RegisteredWaitHandle? _activateRegistration;
    private RegisteredWaitHandle? _exitRegistration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate, EventWaitHandle exit)
    {
        _mutex = mutex;
        _activate = activate;
        _exit = exit;
    }

    /// <summary>Raised on a thread-pool thread when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Raised on a thread-pool thread when the installer (update/uninstall hook) asked this instance to exit.</summary>
    public event EventHandler? ExitRequested;

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

        return new SingleInstanceGuard(
            mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName),
            new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName));
    }

    /// <summary>
    /// From another process (installer hooks): asks a running DualAudio to exit through its normal Exit path (phone link
    /// released, tray icon removed) and waits until it has. True when none was running or it exited within
    /// <paramref name="timeout"/>.
    /// </summary>
    public static bool RequestRunningInstanceToExit(TimeSpan timeout)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex))
        {
            return true;
        }

        using (mutex)
        {
            using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
            {
                exit.Set();
            }

            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The instance ended without releasing it: it's gone either way, and we now own it.
            }

            mutex.ReleaseMutex();
            return true;
        }
    }

    public void StartListening()
    {
        _activateRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        _exitRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _exit,
            (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Must run on the thread that called <see cref="TryAcquire"/> (mutex ownership is per thread).</summary>
    public void Dispose()
    {
        _activateRegistration?.Unregister(null);
        _exitRegistration?.Unregister(null);
        _activate.Dispose();
        _exit.Dispose();
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

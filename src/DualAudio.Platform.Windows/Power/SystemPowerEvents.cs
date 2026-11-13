using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace DualAudio.Platform.Windows.Power;

/// <summary>
/// Sleep/resume (<see cref="SystemEvents.PowerModeChanged"/>), lock/unlock (<see cref="SystemEvents.SessionSwitch"/>)
/// and logoff/shutdown (<see cref="SystemEvents.SessionEnding"/>).
/// <para>SystemEvents are static, so <see cref="Dispose"/> must unsubscribe or the handlers leak. Subscribing from an
/// MTA thread makes SystemEvents create its own hidden window on a dedicated message-loop thread, so delivery doesn't
/// depend on the WPF dispatcher; events are raised on that thread.</para>
/// </summary>
public sealed class SystemPowerEvents : ISystemPowerEvents
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private bool _subscribed;
    private bool _disposed;

    public SystemPowerEvents(ILogger<SystemPowerEvents>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public event EventHandler? SessionLocked;

    public event EventHandler? SessionUnlocked;

    public event EventHandler? SessionEnding;

    public void Start()
    {
        lock (_sync)
        {
            if (_subscribed || _disposed)
            {
                return;
            }

            _subscribed = true;
        }

        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                // Don't tie the hidden SystemEvents window to the UI thread.
                Task.Run(Subscribe).GetAwaiter().GetResult();
            }
            else
            {
                Subscribe();
            }

            _logger.LogInformation("Listening for sleep/resume, lock/unlock and logoff/shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscribing to system power/session events failed; sleep/resume recovery relies on the periodic link check");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_subscribed)
            {
                return;
            }
        }

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unsubscribing system events failed");
        }
    }

    private void Subscribe()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                SafeEvent.Raise(Suspending, this, _logger, nameof(Suspending));
                break;
            case PowerModes.Resume:
                SafeEvent.Raise(Resumed, this, _logger, nameof(Resumed));
                break;
            default:
                _logger.LogDebug("Power mode {Mode}", e.Mode);
                break;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                SafeEvent.Raise(SessionLocked, this, _logger, nameof(SessionLocked));
                break;
            case SessionSwitchReason.SessionUnlock:
                SafeEvent.Raise(SessionUnlocked, this, _logger, nameof(SessionUnlocked));
                break;
            default:
                _logger.LogDebug("Session switch {Reason}", e.Reason);
                break;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _logger.LogInformation("Session ending ({Reason})", e.Reason);

        // Handlers finish their work before returning (the coordinator closes the connection, bounded).
        SafeEvent.Raise(SessionEnding, this, _logger, nameof(SessionEnding));
    }
}

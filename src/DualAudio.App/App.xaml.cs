using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DualAudio.App.Composition;
using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DualAudio.App;

/// <summary>
/// DualAudio lives in the tray. Start-up: tray icon, global hotkeys, start-with-Windows entry kept current, then
/// onboarding (no saved phone, see <see cref="OnboardingPolicy"/>) or the dashboard (not shown with --minimized).
/// Closing the dashboard hides it (<see cref="ShellViewModel.OnMainWindowClosing"/>); only Exit ends the process:
/// it releases everything (coordinator and receiver disposal awaited), then removes the tray icon.
/// </summary>
public partial class App : IAppShell
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private readonly SingleInstanceGuard _instance;
    private readonly bool _startMinimized;
    private readonly ILogger _logger;
    private MainWindow? _window;
    private OnboardingWindow? _onboarding;
    private OnboardingViewModel? _onboardingViewModel;
    private TrayIcon? _tray;
    private bool _onboardingFinished;
    private bool _dashboardInitialized;
    private bool _shuttingDown;
    private bool _shutdownComplete;

    public App(IHost host, SingleInstanceGuard instance, bool startMinimized)
    {
        _host = host;
        _instance = instance;
        _startMinimized = startMinimized;
        _logger = host.Services.GetRequiredService<ILogger<App>>();
    }

    private ShellViewModel Shell => _host.Services.GetRequiredService<ShellViewModel>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var services = _host.Services;

            // Construct the Core services and platform adapters off the UI thread (no Bluetooth/audio work on it).
            await Task.Run(() =>
            {
                StartupChecks.Verify(services);
                services.GetRequiredService<PhoneAudioService>();
                services.GetRequiredService<IOutputDeviceService>();
                services.GetRequiredService<ISystemSettingsLauncher>();
                services.GetRequiredService<DualAudioRuntime>();
                services.GetRequiredService<HotkeyManager>();
            });
            await _host.StartAsync();
            var settings = await Task.Run(() => services.GetRequiredService<ISettingsStore>().LoadAsync(CancellationToken.None));
            _logger.LogInformation("Starting{Minimized}", _startMinimized ? " minimized (tray only)" : string.Empty);

            // Constructed (and subscribed to the services) before the platform starts raising events.
            services.GetRequiredService<DashboardViewModel>();
            await Shell.InitializeAsync();
            Shell.SettingsChanged += OnSettingsChanged;

            _tray = services.GetRequiredService<TrayIcon>();
            _tray.Show();

            _instance.ActivationRequested += OnActivationRequested;
            _instance.StartListening();

            // Rewritten on every start while enabled, so a moved exe keeps starting with Windows.
            var autoStart = services.GetRequiredService<AutoStartRegistration>();
            _ = Task.Run(() => autoStart.Sync(settings.StartWithWindows, Environment.ProcessPath));

            await services.GetRequiredService<HotkeyManager>().ApplyFromSettingsAsync();

            var runtime = services.GetRequiredService<DualAudioRuntime>();
            if (OnboardingPolicy.ShouldShow(settings))
            {
                // Shown even with --minimized: without a saved phone there is nothing to do in the tray.
                _logger.LogInformation("No saved phone: showing onboarding");
                var onboarding = _onboardingViewModel = services.GetRequiredService<OnboardingViewModel>();
                onboarding.Finished += OnOnboardingFinished;
                _onboarding = services.GetRequiredService<OnboardingWindow>();
                _onboarding.Closing += OnOnboardingClosing;
                MainWindow = _onboarding;
                _onboarding.Show();

                // The checks need the radio monitor and the watcher running; nothing to auto-connect yet.
                _ = Task.Run(runtime.StartAsync);
                await onboarding.InitializeAsync();
            }
            else
            {
                await InitializeDashboardAsync();
                if (!_startMinimized)
                {
                    ShowMainWindow();
                }

                _ = Task.Run(runtime.StartAsync);
            }

            _logger.LogInformation("DualAudio started");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "DualAudio failed to start");
            MessageBox.Show(
                "DualAudio couldn't start. Details are in the log file in %LOCALAPPDATA%\\DualAudio\\logs.",
                "DualAudio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _shutdownComplete = true;
            _tray?.Dispose();
            Shutdown(1);
        }
    }

    // ---------------------------------------------------------------- IAppShell

    public void ShowMainWindow()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (_onboarding is not null)
        {
            _onboarding.BringToFront(); // finish setup first
            return;
        }

        if (_window is null)
        {
            _window = _host.Services.GetRequiredService<MainWindow>();
            _window.Closing += OnMainWindowClosing;
            MainWindow = _window;
        }

        _window.BringToFront();
    }

    public async Task ExitAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _window?.Hide();
        _onboarding?.Hide();
        await ShutdownServicesAsync();

        // Only after the connection is released: the tray icon is the last thing to go.
        try
        {
            _tray?.Dispose();
        }
        catch (Exception)
        {
            // Nothing left to log to.
        }

        _shutdownComplete = true;
        Shutdown();
    }

    // ---------------------------------------------------------------- windows

    private async Task InitializeDashboardAsync()
    {
        if (_dashboardInitialized)
        {
            return;
        }

        _dashboardInitialized = true;
        await _host.Services.GetRequiredService<DashboardViewModel>().InitializeAsync();
    }

    /// <summary>Onboarding done (or skipped): swap to the dashboard. The connection is kept (owned by the coordinator).</summary>
    private async void OnOnboardingFinished(object? sender, OnboardingOutcome outcome)
    {
        try
        {
            _logger.LogInformation("Onboarding finished: {Outcome}", outcome);
            if (_onboardingViewModel is not { } viewModel || _shuttingDown)
            {
                return;
            }

            viewModel.Finished -= OnOnboardingFinished;
            await viewModel.SaveTask; // the saved phone must be on disk before the dashboard reads settings

            _onboardingFinished = true;
            var window = _onboarding;
            _onboarding = null;
            await InitializeDashboardAsync();
            Shell.OpenMainCommand.Execute(null); // on the dashboard, even if tray "Settings" was used during setup
            window?.Close();
            viewModel.Dispose();
            _onboardingViewModel = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching from onboarding to the dashboard failed");
        }
    }

    /// <summary>Closing the dashboard hides it to the tray; the first time, a notification explains that.</summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        var decision = Shell.OnMainWindowClosing();
        if (decision == CloseDecision.Close)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        if (decision == CloseDecision.HideToTrayWithNotice)
        {
            _tray?.ShowNotice(ShellViewModel.NoticeTitle, ShellViewModel.NoticeMessage);
        }
    }

    /// <summary>Closing onboarding before it finished exits DualAudio (there is no saved phone to run with).</summary>
    private void OnOnboardingClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete || _onboardingFinished)
        {
            return;
        }

        e.Cancel = true;
        _ = Shell.ExitCommand.ExecuteAsync(null);
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            await _host.Services.GetRequiredService<DashboardViewModel>().ReloadSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reloading the dashboard after a settings change failed");
        }
    }

    // ---------------------------------------------------------------- shutdown

    private async Task ShutdownServicesAsync()
    {
        _logger.LogInformation("Exiting: releasing the phone connection");
        _instance.ActivationRequested -= OnActivationRequested;
        var services = _host.Services;
        try
        {
            services.GetRequiredService<HotkeyManager>().Dispose(); // unregisters the global shortcuts
            services.GetRequiredService<DashboardViewModel>().Dispose();
            _onboardingViewModel?.Dispose();
            await services.GetRequiredService<DualAudioRuntime>().StopAsync().WaitAsync(ShutdownTimeout);
            await _host.StopAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutdown did not complete cleanly");
        }

        _logger.LogInformation("Shutdown complete");
        try
        {
            // Disposes the adapters (watcher, receiver, radio, Core Audio, hotkey window) and flushes the log file.
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
        catch (Exception)
        {
            // The logger is gone at this point; nothing more to do.
        }
    }

    private void OnActivationRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowMainWindow);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}

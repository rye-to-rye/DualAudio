using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DualAudio.App.Composition;
using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DualAudio.App;

/// <summary>
/// Shows onboarding (first run / no saved phone, see <see cref="OnboardingPolicy"/>) or the dashboard, starts the
/// platform off the UI thread, and on close releases everything (coordinator and receiver disposal awaited) before the
/// process exits. Closing a window exits the app (the tray comes later); finishing onboarding opens the dashboard.
/// </summary>
public partial class App
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private readonly SingleInstanceGuard _instance;
    private readonly ILogger _logger;
    private MainWindow? _window;
    private OnboardingWindow? _onboarding;
    private OnboardingViewModel? _onboardingViewModel;
    private bool _onboardingFinished;
    private bool _shuttingDown;
    private bool _shutdownComplete;

    public App(IHost host, SingleInstanceGuard instance)
    {
        _host = host;
        _instance = instance;
        _logger = host.Services.GetRequiredService<ILogger<App>>();
    }

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
            });
            await _host.StartAsync();
            var settings = await Task.Run(() => services.GetRequiredService<ISettingsStore>().LoadAsync(CancellationToken.None));

            // Constructed (and subscribed to the services) before the platform starts raising events.
            services.GetRequiredService<DashboardViewModel>();

            _instance.ActivationRequested += OnActivationRequested;
            _instance.StartListening();

            var runtime = services.GetRequiredService<DualAudioRuntime>();
            if (OnboardingPolicy.ShouldShow(settings))
            {
                _logger.LogInformation("No saved phone: showing onboarding");
                var onboarding = _onboardingViewModel = services.GetRequiredService<OnboardingViewModel>();
                onboarding.Finished += OnOnboardingFinished;
                _onboarding = services.GetRequiredService<OnboardingWindow>();
                _onboarding.Closing += OnAppWindowClosing;
                MainWindow = _onboarding;
                _onboarding.Show();

                // The checks need the radio monitor and the watcher running; nothing to auto-connect yet.
                _ = Task.Run(runtime.StartAsync);
                await onboarding.InitializeAsync();
            }
            else
            {
                await ShowDashboardAsync();
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
            Shutdown(1);
        }
    }

    private async Task ShowDashboardAsync()
    {
        var services = _host.Services;
        _window = services.GetRequiredService<MainWindow>();
        _window.Closing += OnAppWindowClosing;
        MainWindow = _window;
        _window.Show();
        await services.GetRequiredService<DashboardViewModel>().InitializeAsync();
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
            await ShowDashboardAsync();
            window?.Close();
            viewModel.Dispose();
            _onboardingViewModel = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching from onboarding to the dashboard failed");
        }
    }

    private async void OnAppWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        if (_onboardingFinished && sender is OnboardingWindow)
        {
            return; // onboarding closes itself after opening the dashboard
        }

        // Keep the process alive until the connection is released (BUG-001), then exit.
        e.Cancel = true;
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _window?.Hide();
        _onboarding?.Hide();
        await ShutdownServicesAsync();
        _shutdownComplete = true;
        Shutdown();
    }

    private async Task ShutdownServicesAsync()
    {
        _logger.LogInformation("Exiting: releasing the phone connection");
        _instance.ActivationRequested -= OnActivationRequested;
        var services = _host.Services;
        try
        {
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
            // Disposes the adapters (watcher, receiver, radio, Core Audio) and flushes the log file.
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

    private void OnActivationRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_shuttingDown)
            {
                ((Window?)_onboarding ?? _window)?.BringToFront();
            }
        });

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

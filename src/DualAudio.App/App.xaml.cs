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
/// Shows the dashboard, starts the platform off the UI thread, and on close releases everything (coordinator and
/// receiver disposal awaited) before the process exits. Closing the window exits the app (the tray comes later).
/// </summary>
public partial class App
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private readonly SingleInstanceGuard _instance;
    private readonly ILogger _logger;
    private MainWindow? _window;
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

            var viewModel = services.GetRequiredService<DashboardViewModel>();
            _window = services.GetRequiredService<MainWindow>();
            _window.Closing += OnMainWindowClosing;
            MainWindow = _window;
            _window.Show();

            _instance.ActivationRequested += OnActivationRequested;
            _instance.StartListening();

            await viewModel.InitializeAsync();
            var runtime = services.GetRequiredService<DualAudioRuntime>();
            _ = Task.Run(runtime.StartAsync);
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

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        // Keep the process alive until the connection is released (BUG-001), then exit.
        e.Cancel = true;
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _window?.Hide();
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
                _window?.BringToFront();
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

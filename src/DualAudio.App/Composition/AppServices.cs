using System.Windows;
using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Startup;
using DualAudio.Infrastructure;
using DualAudio.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DualAudio.App.Composition;

/// <summary>The composition root's registrations, separate from Program so tests can build them.</summary>
public static class AppServices
{
    public static IServiceCollection AddDualAudioApp(this IServiceCollection services, AppPaths paths)
    {
        services.AddDualAudioInfrastructure(paths);
        services.AddWindowsPlatform();
        services.AddDualAudioCore();

        services.TryAddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current.Dispatcher));
        services.TryAddSingleton<ISystemSettingsLauncher, SystemSettingsLauncher>();
        services.TryAddSingleton<DualAudioRuntime>();
        services.TryAddSingleton<DashboardViewModel>();
        services.TryAddSingleton<MainWindow>();
        services.TryAddSingleton<OnboardingViewModel>();
        services.TryAddSingleton<OnboardingWindow>();

        // Tray, hotkeys, settings (Prompt 8).
        services.TryAddSingleton<IHotkeyService>(sp =>
            new Win32HotkeyService(Application.Current.Dispatcher, sp.GetService<ILogger<Win32HotkeyService>>()));
        services.TryAddSingleton<IAppShell>(_ => (IAppShell)Application.Current);
        services.TryAddSingleton<IConfirmDialog, MessageBoxConfirmDialog>();
        services.TryAddSingleton(sp => new ShellViewModel(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IAppShell>(),
            sp.GetService<ILogger<ShellViewModel>>(),
            () => sp.GetRequiredService<SettingsViewModel>()));
        services.TryAddSingleton<TrayViewModel>();
        services.TryAddSingleton<TrayIcon>();
        services.TryAddTransient<SettingsViewModel>();
        return services;
    }

    /// <summary>
    /// Core services. The coordinator MUST get the <see cref="IAudioSessionController"/> (its optional argument):
    /// without it the BUG-001 stale-session priming silently does nothing. <see cref="StartupChecks"/> asserts this.
    /// </summary>
    public static IServiceCollection AddDualAudioCore(this IServiceCollection services)
    {
        services.TryAddSingleton(new ConnectionCoordinatorOptions());
        services.TryAddSingleton(new PhoneAudioOptions());
        services.TryAddSingleton(sp => new ConnectionCoordinator(
            sp.GetRequiredService<IBluetoothAudioSourceWatcher>(),
            sp.GetRequiredService<IAudioReceiver>(),
            sp.GetRequiredService<IRadioMonitor>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ConnectionCoordinatorOptions>(),
            sp.GetRequiredService<ILogger<ConnectionCoordinator>>(),
            sessions: sp.GetRequiredService<IAudioSessionController>(),
            power: sp.GetService<ISystemPowerEvents>()));
        services.TryAddSingleton(new ConnectionDiagnosticsOptions());
        services.TryAddSingleton(sp => new ConnectionDiagnostics(
            sp.GetRequiredService<ConnectionCoordinator>(),
            sp.GetRequiredService<IAudioSessionController>(),
            sp.GetRequiredService<IOutputDeviceService>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ConnectionDiagnosticsOptions>(),
            sp.GetRequiredService<ILogger<ConnectionDiagnostics>>()));
        services.TryAddSingleton(sp => new PhoneAudioService(
            sp.GetRequiredService<IAudioSessionController>(),
            sp.GetRequiredService<ConnectionCoordinator>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<PhoneAudioOptions>(),
            sp.GetRequiredService<ILogger<PhoneAudioService>>()));
        services.TryAddSingleton<HotkeyManager>();
        services.TryAddSingleton<AutoStartRegistration>();
        return services;
    }
}

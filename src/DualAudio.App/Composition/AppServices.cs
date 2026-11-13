using System.Windows;
using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
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
            sessions: sp.GetRequiredService<IAudioSessionController>()));
        services.TryAddSingleton(sp => new PhoneAudioService(
            sp.GetRequiredService<IAudioSessionController>(),
            sp.GetRequiredService<ConnectionCoordinator>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<PhoneAudioOptions>(),
            sp.GetRequiredService<ILogger<PhoneAudioService>>()));
        return services;
    }
}

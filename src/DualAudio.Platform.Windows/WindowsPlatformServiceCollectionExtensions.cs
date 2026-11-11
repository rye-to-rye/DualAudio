using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Bluetooth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DualAudio.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Windows adapters as singletons. Nothing touches WinRT until the composition root calls
    /// <see cref="IRadioMonitor.StartAsync"/> and <see cref="IBluetoothAudioSourceWatcher.Start"/>; construct
    /// the ConnectionCoordinator first so it is subscribed before the first events arrive.
    /// </summary>
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<A2dpSourceWatcher>();
        services.TryAddSingleton<IBluetoothAudioSourceWatcher>(sp => sp.GetRequiredService<A2dpSourceWatcher>());
        services.TryAddSingleton<IAudioReceiver, PlaybackConnectionReceiver>();
        services.TryAddSingleton<IRadioMonitor, RadioMonitor>();
        services.TryAddSingleton<BluetoothSettingsLauncher>();
        return services;
    }
}

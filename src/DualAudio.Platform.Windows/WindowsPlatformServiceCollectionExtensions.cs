using DualAudio.Core.Abstractions;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Startup;
using DualAudio.Core.Updates;
using DualAudio.Platform.Windows.Audio;
using DualAudio.Platform.Windows.Bluetooth;
using DualAudio.Platform.Windows.Diagnostics;
using DualAudio.Platform.Windows.Power;
using DualAudio.Platform.Windows.Startup;
using DualAudio.Platform.Windows.Updates;
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
        services.TryAddSingleton<ICapabilityService, CapabilityService>();
        services.TryAddSingleton<IBluetoothRadioControl, BluetoothRadioControl>(); // user-initiated "Turn on Bluetooth" only
        services.TryAddSingleton<IAutoStartEntry, RunKeyAutoStartEntry>(); // HKCU Run value; touched only via AutoStartRegistration
        services.TryAddSingleton<ISystemPowerEvents, SystemPowerEvents>(); // sleep/resume, lock/unlock, logoff (Start() subscribes)
        services.TryAddSingleton<IProcessResources, ProcessResourceReader>();
        services.TryAddSingleton<ISystemInfoProvider, SystemInfoProvider>(); // bug report only; read-only
        services.TryAddSingleton<IAppUpdater, VelopackUpdater>(); // GitHub Releases; inert when not installed

        // Core Audio is first touched by the first call on these (no COM in constructors except the worker thread).
        services.TryAddSingleton<IAudioSessionController, NAudioSessionController>();
        services.TryAddSingleton<OutputDeviceService>();
        services.TryAddSingleton<IOutputDeviceService>(sp => sp.GetRequiredService<OutputDeviceService>());
        services.TryAddSingleton<DefaultEndpointMasterVolume>(); // optional, whole-device volume; not phone-only
        return services;
    }
}

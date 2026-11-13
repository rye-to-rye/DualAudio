using DualAudio.Core.Connection;
using Microsoft.Extensions.DependencyInjection;

namespace DualAudio.App.Composition;

public static class StartupChecks
{
    /// <summary>Fails fast if the composition is missing something that would otherwise fail silently.</summary>
    /// <exception cref="InvalidOperationException">The coordinator has no IAudioSessionController.</exception>
    public static void Verify(IServiceProvider services)
    {
        var coordinator = services.GetRequiredService<ConnectionCoordinator>();
        if (!coordinator.IsStaleSessionPrimingEnabled)
        {
            throw new InvalidOperationException(
                "ConnectionCoordinator was built without an IAudioSessionController, so the BUG-001 stale-session " +
                "priming would silently do nothing. Pass it as the coordinator's 'sessions' argument.");
        }
    }
}

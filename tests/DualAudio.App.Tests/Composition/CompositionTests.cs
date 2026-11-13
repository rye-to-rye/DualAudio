using DualAudio.App.Composition;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Infrastructure;
using DualAudio.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DualAudio.App.Tests.Composition;

public sealed class CompositionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DualAudioTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // The rolling log file may still be held briefly.
        }
    }

    [Fact]
    public async Task App_composition_gives_the_coordinator_the_session_controller_BUG_001()
    {
        await using var provider = new ServiceCollection().AddDualAudioApp(new AppPaths(_dir)).BuildServiceProvider();

        var coordinator = provider.GetRequiredService<ConnectionCoordinator>();

        coordinator.IsStaleSessionPrimingEnabled.Should().BeTrue();
        var verify = () => StartupChecks.Verify(provider);
        verify.Should().NotThrow();
        provider.GetRequiredService<PhoneAudioService>().Should().NotBeNull();
        provider.GetRequiredService<ISettingsStore>().Should().BeOfType<JsonSettingsStore>()
            .Which.FilePath.Should().Be(Path.Combine(_dir, "settings.json"));
    }

    [Fact]
    public async Task Startup_check_fails_when_the_coordinator_has_no_session_controller()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ConnectionCoordinator(
            Substitute.For<IBluetoothAudioSourceWatcher>(),
            Substitute.For<IAudioReceiver>(),
            Substitute.For<IRadioMonitor>(),
            Substitute.For<IClock>()));
        await using var provider = services.BuildServiceProvider();

        var verify = () => StartupChecks.Verify(provider);

        verify.Should().Throw<InvalidOperationException>().WithMessage("*IAudioSessionController*BUG-001*");
    }
}

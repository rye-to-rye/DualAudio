using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows;
using DualAudio.Platform.Windows.Audio;
using DualAudio.Platform.Windows.Bluetooth;
using Microsoft.Extensions.DependencyInjection;

namespace DualAudio.Platform.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddWindowsPlatform_resolves_every_adapter_without_touching_hardware()
    {
        using var provider = new ServiceCollection().AddWindowsPlatform().BuildServiceProvider();

        provider.GetRequiredService<IBluetoothAudioSourceWatcher>().Should().BeOfType<A2dpSourceWatcher>()
            .And.BeSameAs(provider.GetRequiredService<A2dpSourceWatcher>());
        provider.GetRequiredService<IAudioReceiver>().Should().BeOfType<PlaybackConnectionReceiver>();
        provider.GetRequiredService<IRadioMonitor>().Should().BeOfType<RadioMonitor>();
        provider.GetRequiredService<IClock>().Should().BeOfType<SystemClock>();
        provider.GetRequiredService<BluetoothSettingsLauncher>().Should().NotBeNull();
        provider.GetRequiredService<IAudioSessionController>().Should().BeOfType<NAudioSessionController>();
        provider.GetRequiredService<IOutputDeviceService>().Should().BeOfType<OutputDeviceService>()
            .And.BeSameAs(provider.GetRequiredService<OutputDeviceService>());
        provider.GetRequiredService<DefaultEndpointMasterVolume>().Should().NotBeNull();
    }

    [Fact]
    public async Task Receiver_reports_Closed_and_close_is_safe_when_nothing_is_open()
    {
        using var receiver = new PlaybackConnectionReceiver();

        receiver.State.Should().Be(ReceiverState.Closed);
        await receiver.CloseAsync();
        receiver.State.Should().Be(ReceiverState.Closed);
    }

    [Fact]
    public async Task Receiver_open_with_cancelled_token_throws_OperationCanceled()
    {
        using var receiver = new PlaybackConnectionReceiver();

        var open = () => receiver.OpenAsync("some-id", new CancellationToken(canceled: true));

        await open.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Watcher_start_and_stop_are_idempotent()
    {
        using var watcher = new A2dpSourceWatcher();

        watcher.Stop();
        watcher.Start();
        watcher.Start();
        watcher.Stop();
        watcher.Stop();

        watcher.Devices.Should().NotBeNull();
    }
}

using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>Prompt 9: "Fix audio" shows only in the yellow Connected-but-not-receiving state and runs the coordinator's fix.</summary>
public sealed class FixAudioLinkTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task Fix_audio_is_shown_only_while_Connected_and_not_receiving()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Vm.IsFixAudioVisible.Should().BeFalse("not connected");
        f.Vm.FixAudioCommand.CanExecute(null).Should().BeFalse();

        await f.Vm.ConnectCommand.ExecuteAsync(null);
        await f.SettleAsync();
        f.Vm.IsFixAudioVisible.Should().BeFalse("connecting is yellow too, but not 'connected without audio'");

        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();
        f.Vm.ConnectionState.Should().Be(ConnectionState.Connected);
        f.Vm.StatusTone.Should().Be(StatusTone.Warn);
        f.Vm.IsFixAudioVisible.Should().BeTrue();
        f.Vm.FixAudioCommand.CanExecute(null).Should().BeTrue();

        f.H.SetPhoneSession(active: true, peak: 0.2f);
        await f.AdvanceAsync(Poll);
        f.Vm.IsReceivingAudio.Should().BeTrue();
        f.Vm.IsFixAudioVisible.Should().BeFalse("green: audio is arriving");
    }

    [Fact]
    public async Task Fix_audio_reconnects_through_the_coordinator()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.Vm.ConnectCommand.ExecuteAsync(null);
        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();

        await f.Vm.FixAudioCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Vm.ConnectionState.Should().Be(ConnectionState.Opening);
        f.Vm.IsFixAudioVisible.Should().BeFalse();
        f.H.CloseCalls.Should().Be(1);

        f.H.SetReceiverStateSilently(ReceiverState.Closed);
        await f.AdvanceAsync(TimeSpan.FromSeconds(3));
        f.H.OpenCalls.Should().Be(2);
    }
}

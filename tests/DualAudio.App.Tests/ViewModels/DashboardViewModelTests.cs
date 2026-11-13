using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task Without_phones_nothing_can_connect_and_status_is_Disconnected()
    {
        await using var f = await DashboardFixture.CreateAsync(withPhone: false);

        f.Vm.Devices.Should().BeEmpty();
        f.Vm.ConnectCommand.CanExecute(null).Should().BeFalse();
        f.Vm.IsConnectVisible.Should().BeTrue();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeFalse();
        f.Vm.StatusKind.Should().Be(StatusKind.Disconnected);
        f.Vm.StatusText.Should().Be("Disconnected");
        f.Vm.IsErrorVisible.Should().BeFalse();
        f.Vm.OutputDeviceName.Should().Be("Headset Earphone");
    }

    [Fact]
    public async Task Added_phone_is_listed_selected_and_connectable()
    {
        await using var f = await DashboardFixture.CreateAsync();

        f.Vm.Devices.Should().ContainSingle().Which.Should().Be(new DeviceItem(PhoneId, PhoneName));
        f.Vm.SelectedDevice!.Id.Should().Be(PhoneId);
        f.Vm.ConnectCommand.CanExecute(null).Should().BeTrue();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Remembered_phone_is_preselected()
    {
        await using var f = new DashboardFixture(AppSettings.Default with { PreferredDeviceId = OtherPhoneId });
        await f.H.AddDeviceAsync(PhoneId, "Alpha");
        await f.H.AddDeviceAsync(OtherPhoneId, "Bravo");
        await f.Vm.InitializeAsync();

        f.Vm.SelectedDevice!.Id.Should().Be(OtherPhoneId);
    }

    [Fact]
    public async Task Connect_disables_both_buttons_while_Opening_then_Connected_enables_Disconnect()
    {
        await using var f = await DashboardFixture.CreateAsync();

        await f.Vm.ConnectCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Vm.ConnectionState.Should().Be(ConnectionState.Opening);
        f.Vm.IsTransitioning.Should().BeTrue();
        f.Vm.StatusKind.Should().Be(StatusKind.Connecting);
        f.Vm.StatusText.Should().Be("Connecting…");
        f.Vm.ConnectCommand.CanExecute(null).Should().BeFalse();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeFalse("disabled while transitioning");

        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();

        f.Vm.ConnectionState.Should().Be(ConnectionState.Connected);
        f.Vm.StatusKind.Should().Be(StatusKind.Connected);
        f.Vm.StatusText.Should().Be("Connected");
        f.Vm.IsDisconnectVisible.Should().BeTrue();
        f.Vm.IsConnectVisible.Should().BeFalse();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeTrue();
        f.Vm.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Connect_remembers_the_phone_for_the_next_start()
    {
        await using var f = await DashboardFixture.CreateAsync(AppSettings.Default with { PhoneVolume = 40 });

        await f.Vm.ConnectCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Store.Current.PreferredDeviceId.Should().Be(PhoneId);
        f.Store.Current.PhoneVolume.Should().Be(40, "other settings are kept");
    }

    [Fact]
    public async Task Disconnect_returns_to_Disconnected_and_Connect_is_available_again()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        await f.SettleAsync();

        await f.Vm.DisconnectCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Vm.ConnectionState.Should().Be(ConnectionState.Disconnected);
        f.Vm.StatusText.Should().Be("Disconnected");
        f.Vm.IsConnectVisible.Should().BeTrue();
        f.Vm.ConnectCommand.CanExecute(null).Should().BeTrue();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Waiting_for_phone_is_Connecting_but_can_be_cancelled()
    {
        await using var f = await DashboardFixture.CreateAsync(withPhone: false);
        f.H.Watcher.IsEnumerationCompleted.Returns(false);

        await f.H.Sut.ConnectAsync(PhoneId);
        await f.SettleAsync();

        f.Vm.StatusKind.Should().Be(StatusKind.Connecting);
        f.Vm.StatusText.Should().Be("Waiting for phone…");
        f.Vm.IsTransitioning.Should().BeFalse();
        f.Vm.DisconnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Receiving_audio_is_shown_from_PhoneAudioService()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.2f);

        await f.AdvanceAsync(Poll);

        f.Vm.IsReceivingAudio.Should().BeTrue();
        f.Vm.StatusKind.Should().Be(StatusKind.Receiving);
        f.Vm.StatusText.Should().Be("Receiving audio");
    }

    [Fact]
    public async Task Mute_goes_to_the_service_is_persisted_and_shows_Muted()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true);
        await f.AdvanceAsync(Poll);

        f.Vm.IsMuted = true;
        await f.SettleAsync();

        await f.H.Sessions.Received().SetPhoneMutedAsync(true, Arg.Any<CancellationToken>());
        f.Store.Current.PhoneMuted.Should().BeTrue();
        f.PhoneAudio.Muted.Should().BeTrue();
        f.Vm.IsMuted.Should().BeTrue();
        f.Vm.StatusText.Should().Be("Connected · Muted");
    }

    [Fact]
    public async Task Volume_goes_to_the_service_and_is_persisted()
    {
        await using var f = await DashboardFixture.CreateAsync();

        f.Vm.PhoneVolume = 30;
        await f.SettleAsync();

        await f.H.Sessions.Received().SetPhoneVolumeAsync(30, Arg.Any<CancellationToken>());
        f.Store.Current.PhoneVolume.Should().Be(30);
        f.Vm.PhoneVolume.Should().Be(30);
    }

    [Fact]
    public async Task Saved_volume_and_mute_are_shown_at_start()
    {
        await using var f = await DashboardFixture.CreateAsync(AppSettings.Default with { PhoneVolume = 25, PhoneMuted = true });

        f.Vm.PhoneVolume.Should().Be(25);
        f.Vm.IsMuted.Should().BeTrue();
        await f.H.Sessions.DidNotReceive().SetPhoneVolumeAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Volume_controls_are_disabled_with_a_tooltip_until_a_phone_session_exists()
    {
        await using var f = await DashboardFixture.CreateAsync();

        f.Vm.IsPhoneAudioAvailable.Should().BeFalse();
        f.Vm.PhoneAudioToolTip.Should().Be(DashboardViewModel.PhoneAudioUnavailableToolTip);

        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true);
        await f.AdvanceAsync(Poll);

        f.Vm.IsPhoneAudioAvailable.Should().BeTrue();
        f.Vm.PhoneAudioToolTip.Should().BeNull();
    }

    [Fact]
    public async Task Open_failure_0x8007001F_shows_cant_reach_your_phone_with_Retry()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.H.SetOpenFailure(ErrorKind.PhoneUnreachable);

        await f.Vm.ConnectCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Vm.StatusKind.Should().Be(StatusKind.Error);
        f.Vm.IsErrorVisible.Should().BeTrue();
        f.Vm.Error!.Title.Should().Be("Can't reach your phone");
        f.Vm.Error.ActionLabel.Should().Be("Retry");
        f.Vm.ConnectCommand.CanExecute(null).Should().BeTrue();

        f.H.SetOpenResult(OpenResult.Ok());
        await f.Vm.ErrorActionCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.H.OpenCalls.Should().Be(2);
        f.Vm.ConnectionState.Should().Be(ConnectionState.Opening);
        f.Vm.IsErrorVisible.Should().BeFalse();
    }

    [Fact]
    public async Task PC_Bluetooth_off_shows_a_banner_that_opens_Bluetooth_settings_and_clears_when_back_on()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();

        f.H.RaiseRadio(RadioStatus.Off);
        await f.SettleAsync();

        f.Vm.StatusKind.Should().Be(StatusKind.Error);
        f.Vm.Error!.Kind.Should().Be(ErrorKind.BluetoothOff);
        f.Vm.Error.ActionLabel.Should().Be("Open Bluetooth settings");
        await f.Vm.ErrorActionCommand.ExecuteAsync(null);
        await f.Launcher.Received(1).OpenBluetoothSettingsAsync();

        f.H.RaiseRadio(RadioStatus.On);
        await f.SettleAsync();

        f.Vm.IsErrorVisible.Should().BeFalse();
        f.Vm.StatusKind.Should().Be(StatusKind.Connecting, "the coordinator resumes the connection on its own");
    }

    [Fact]
    public async Task Sink_not_supported_has_no_action_button()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.H.SetOpenFailure(ErrorKind.SinkNotSupported);

        await f.Vm.ConnectCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Vm.IsErrorVisible.Should().BeTrue();
        f.Vm.HasErrorAction.Should().BeFalse();
        f.Vm.ErrorActionCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Add_a_phone_and_per_app_output_open_Windows_settings()
    {
        await using var f = await DashboardFixture.CreateAsync();

        await f.Vm.AddPhoneCommand.ExecuteAsync(null);
        await f.Vm.OpenPerAppOutputSettingsCommand.ExecuteAsync(null);

        await f.Launcher.Received(1).OpenBluetoothSettingsAsync();
        await f.Launcher.Received(1).OpenAppVolumeSettingsAsync();
    }

    [Fact]
    public async Task Default_output_change_updates_the_label()
    {
        await using var f = await DashboardFixture.CreateAsync();

        f.Outputs.DefaultEndpointChanged += Raise.EventWith(f.Outputs, new DefaultEndpointChangedEventArgs(new AudioEndpoint("x", "Speakers", true)));

        f.Vm.OutputDeviceName.Should().Be("Speakers");
    }

    [Fact]
    public async Task Service_events_only_change_the_ViewModel_on_the_UI_dispatcher()
    {
        await using var f = new DashboardFixture(queueUiWork: true);
        await f.H.AddDeviceAsync();
        f.Dispatcher.Pump();

        await f.H.Sut.ConnectAsync(PhoneId);
        await f.SettleAsync();

        f.Dispatcher.Pending.Should().BeGreaterThan(0);
        f.Vm.ConnectionState.Should().Be(ConnectionState.Idle, "nothing is applied until the UI thread runs it");

        f.Dispatcher.Pump();
        f.Vm.ConnectionState.Should().Be(ConnectionState.Opening);
    }

    [Fact]
    public async Task Service_failures_never_throw_into_the_UI()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Launcher.OpenAppVolumeSettingsAsync().Returns(Task.FromException(new InvalidOperationException("boom")));

        var act = () => f.Vm.OpenPerAppOutputSettingsCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
    }
}

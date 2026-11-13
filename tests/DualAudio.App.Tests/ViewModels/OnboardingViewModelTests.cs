using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.ViewModels;

public sealed class OnboardingViewModelTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    // ---------------------------------------------------------------- when it is shown

    [Fact]
    public void Onboarding_is_shown_on_first_run_and_when_no_phone_is_saved_only()
    {
        OnboardingPolicy.ShouldShow(AppSettings.Default).Should().BeTrue("first run");
        OnboardingPolicy.ShouldShow(AppSettings.Default with { PreferredDeviceId = PhoneId }).Should().BeFalse();
        OnboardingPolicy.ShouldShow(AppSettings.Default with { PreferredDeviceId = PhoneId, OnboardingCompleted = true }).Should().BeFalse();
        OnboardingPolicy.ShouldShow(AppSettings.Default.Forget(PhoneId) with { OnboardingCompleted = true })
            .Should().BeTrue("the saved phone was forgotten");
    }

    [Fact]
    public void Skip_is_offered_only_after_a_first_success()
    {
        OnboardingPolicy.CanSkip(AppSettings.Default).Should().BeFalse();
        OnboardingPolicy.CanSkip(AppSettings.Default with { OnboardingCompleted = true }).Should().BeTrue();
    }

    [Fact]
    public async Task Skip_command_follows_the_policy()
    {
        await using (var first = await OnboardingFixture.CreateAsync())
        {
            first.Vm.CanSkip.Should().BeFalse();
            first.Vm.SkipCommand.CanExecute(null).Should().BeFalse();
        }

        await using var again = await OnboardingFixture.CreateAsync(AppSettings.Default with { OnboardingCompleted = true });
        OnboardingOutcome? outcome = null;
        again.Vm.Finished += (_, o) => outcome = o;

        again.Vm.SkipCommand.Execute(null);

        outcome.Should().Be(OnboardingOutcome.Skipped);
    }

    // ---------------------------------------------------------------- step 1: checks

    [Fact]
    public async Task All_checks_green_lets_the_user_continue()
    {
        await using var f = await OnboardingFixture.CreateAsync();

        f.Vm.Step.Should().Be(OnboardingStep.Checks);
        f.Vm.Checks.Should().HaveCount(4).And.OnlyContain(r => r.Passed);
        f.Vm.NextCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Bluetooth_off_is_red_with_a_Turn_on_button_and_blocks_Next_but_is_never_turned_on_by_itself()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: OnboardingFixture.AllGood with { Radio = RadioStatus.Off, AudioSourceCount = 0 });

        var row = f.Vm.Checks.Single(r => r.Title == "Bluetooth is on");
        row.Passed.Should().BeFalse();
        row.FixLabel.Should().Be("Turn on Bluetooth");
        f.Vm.NextCommand.CanExecute(null).Should().BeFalse();
        await f.RadioControl.DidNotReceiveWithAnyArgs().TurnOnAsync(default);
    }

    [Fact]
    public async Task Turn_on_Bluetooth_runs_only_on_the_click_then_checks_again()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: OnboardingFixture.AllGood with { Radio = RadioStatus.Off });
        var row = f.Vm.Checks.Single(r => r.Fix == CheckFix.TurnOnBluetooth);
        f.Report = OnboardingFixture.AllGood;

        await f.Vm.FixCommand.ExecuteAsync(row);
        await f.SettleAsync();

        await f.RadioControl.ReceivedWithAnyArgs(1).TurnOnAsync(default);
        f.Vm.Checks.Should().OnlyContain(r => r.Passed);
        f.Vm.NextCommand.CanExecute(null).Should().BeTrue();
        f.Vm.RadioMessage.Should().BeNull();
    }

    [Fact]
    public async Task Turn_on_Bluetooth_refused_by_Windows_points_to_Settings()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: OnboardingFixture.AllGood with { Radio = RadioStatus.Off });
        f.RadioControl.TurnOnAsync(default).ReturnsForAnyArgs(Task.FromResult(RadioTurnOnResult.AccessDenied));

        await f.Vm.FixCommand.ExecuteAsync(f.Vm.Checks.Single(r => r.Fix == CheckFix.TurnOnBluetooth));

        f.Vm.RadioMessage.Should().Contain("Open Bluetooth settings");
    }

    [Fact]
    public async Task No_adapter_and_old_Windows_are_red_and_block_Next()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: new CapabilityReport(18363, false, RadioStatus.NotPresent, 0, true));

        f.Vm.Checks.Single(r => r.Title == "Windows version").Detail.Should().Contain("19041").And.Contain("18363");
        f.Vm.Checks.Single(r => r.Title == "Bluetooth adapter").FixLabel.Should().Be("Open Bluetooth settings");
        f.Vm.NextCommand.CanExecute(null).Should().BeFalse();

        await f.Vm.FixCommand.ExecuteAsync(f.Vm.Checks.Single(r => r.Title == "Bluetooth adapter"));
        await f.Launcher.Received(1).OpenBluetoothSettingsAsync();
    }

    [Fact]
    public async Task No_paired_phone_yet_is_red_but_does_not_block_pairing()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: OnboardingFixture.AllGood with { AudioSourceCount = 0 }, withPhone: false);

        f.Vm.Checks.Single(r => r.Title == "Paired phone").Passed.Should().BeFalse();
        f.Vm.NextCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Radio_change_reruns_the_checks()
    {
        await using var f = await OnboardingFixture.CreateAsync(report: OnboardingFixture.AllGood with { Radio = RadioStatus.Off });
        f.Report = OnboardingFixture.AllGood;

        f.H.RaiseRadio(RadioStatus.On);
        await f.SettleAsync();

        f.Vm.NextCommand.CanExecute(null).Should().BeTrue();
    }

    // ---------------------------------------------------------------- steps 2 + 3

    [Fact]
    public async Task Pair_step_shows_this_PCs_Bluetooth_name_with_phone_tips()
    {
        await using var f = await OnboardingFixture.CreateAsync();

        await f.Vm.NextCommand.ExecuteAsync(null);

        f.Vm.Step.Should().Be(OnboardingStep.Pair);
        f.Vm.PcBluetoothName.Should().Be(OnboardingFixture.PcName);
        f.Vm.PairInstruction.Should().Be(
            "On your phone, open Bluetooth settings, choose \"GAMING-PC\", and confirm the code on both screens.");
        f.Vm.IPhonePairTip.Should().Contain("iPhone");
        f.Vm.AndroidPairTip.Should().Contain("Android");
    }

    [Fact]
    public async Task Pick_step_waits_for_the_phone_to_appear_in_the_watcher()
    {
        await using var f = await OnboardingFixture.CreateAsync(withPhone: false);
        await f.Vm.NextCommand.ExecuteAsync(null);
        await f.Vm.NextCommand.ExecuteAsync(null);

        f.Vm.Step.Should().Be(OnboardingStep.PickPhone);
        f.Vm.HasDevices.Should().BeFalse();
        f.Vm.NextCommand.CanExecute(null).Should().BeFalse();

        await f.H.AddDeviceAsync(); // the user finished pairing on the phone

        f.Vm.Devices.Should().ContainSingle().Which.Name.Should().Be(PhoneName, "names come from the watcher (name resolver)");
        f.Vm.SelectedDevice!.Id.Should().Be(PhoneId);
        f.Vm.NextCommand.CanExecute(null).Should().BeTrue();
    }

    // ---------------------------------------------------------------- step 4: success requires audio

    [Fact]
    public async Task Connected_alone_is_not_success_IsReceivingAudio_is()
    {
        await using var f = await OnboardingFixture.CreateAsync();
        await f.GoToConnectStepAsync(nickname: "  My phone ");
        f.H.OpenCalls.Should().Be(1);

        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();
        await f.AdvanceAsync(Poll); // a status poll without phone audio

        f.H.Sut.CurrentState.Should().Be(ConnectionState.Connected);
        f.Vm.IsSucceeded.Should().BeFalse();
        f.Vm.ConnectStatus.Should().Be(OnboardingText.PlayNow);
        f.Vm.FinishCommand.CanExecute(null).Should().BeFalse();
        f.Store.Current.PreferredDeviceId.Should().BeNull("nothing is saved before audio arrives");
        f.Store.Current.OnboardingCompleted.Should().BeFalse();

        f.H.SetPhoneSession(active: true, peak: 0.2f); // the user plays something
        await f.AdvanceAsync(Poll);

        f.Vm.IsSucceeded.Should().BeTrue();
        f.Vm.ConnectStatus.Should().Be(OnboardingText.Success);
        f.Vm.FinishCommand.CanExecute(null).Should().BeTrue();
        f.Store.Current.PreferredDeviceId.Should().Be(PhoneId);
        f.Store.Current.OnboardingCompleted.Should().BeTrue();
        f.Store.Current.NicknameFor(PhoneId).Should().Be("My phone", "nickname persisted, trimmed");
    }

    [Fact]
    public async Task Finish_raises_Completed_and_keeps_other_settings()
    {
        await using var f = await OnboardingFixture.CreateAsync(AppSettings.Default with { PhoneVolume = 35, AutoConnectOnStart = false });
        OnboardingOutcome? outcome = null;
        f.Vm.Finished += (_, o) => outcome = o;
        await f.GoToConnectStepAsync();
        f.H.RaiseReceiver(ReceiverState.Opened);
        f.H.SetPhoneSession(active: true, peak: 0.2f);
        await f.AdvanceAsync(Poll);

        f.Vm.FinishCommand.Execute(null);

        outcome.Should().Be(OnboardingOutcome.Completed);
        f.Store.Current.PhoneVolume.Should().Be(35);
        f.Store.Current.AutoConnectOnStart.Should().BeFalse();
        f.Store.Current.DeviceNicknames.Should().BeEmpty("a blank nickname is not stored");
        f.H.Sut.CurrentState.Should().Be(ConnectionState.Connected, "the connection is kept for the dashboard");
    }

    [Fact]
    public async Task No_audio_for_60_s_shows_help_and_audio_arriving_later_still_succeeds()
    {
        await using var f = await OnboardingFixture.CreateAsync();
        await f.GoToConnectStepAsync();
        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();

        await f.AdvanceAsync(TimeSpan.FromSeconds(59));
        f.Vm.IsNoAudioHelpVisible.Should().BeFalse();
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));

        f.Vm.IsNoAudioHelpVisible.Should().BeTrue();
        f.Vm.IsSucceeded.Should().BeFalse();
        f.Vm.IPhoneNoAudioTip.Should().Contain("Control Center").And.Contain(OnboardingFixture.PcName);
        f.Vm.AndroidNoAudioTip.Should().Contain("Media audio");

        f.H.SetPhoneSession(active: true, peak: 0.3f);
        await f.AdvanceAsync(Poll);

        f.Vm.IsSucceeded.Should().BeTrue();
        f.Vm.IsNoAudioHelpVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Works_through_the_BUG_001_priming_reconnect()
    {
        await using var f = await OnboardingFixture.CreateAsync();
        f.H.SetPhoneSession(active: true); // stale session left Active by a previous process, no audio
        await f.GoToConnectStepAsync();

        f.H.RaiseReceiver(ReceiverState.Opened); // priming open
        await f.SettleAsync();
        f.H.RaiseReceiver(ReceiverState.Closed);
        await f.AdvanceAsync(new ConnectionCoordinatorOptions().PrimingDelay);
        f.H.OpenCalls.Should().Be(2, "priming open + real open");
        f.Vm.IsSucceeded.Should().BeFalse();

        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();
        f.H.Sut.CurrentState.Should().Be(ConnectionState.Connected);
        f.Vm.IsSucceeded.Should().BeFalse("a stale Active session without audio is not success");

        f.H.SetPhoneSession(active: true, peak: 0.25f);
        await f.AdvanceAsync(Poll);

        f.Vm.IsSucceeded.Should().BeTrue();
        f.Store.Current.PreferredDeviceId.Should().Be(PhoneId);
    }

    [Fact]
    public async Task Phone_Bluetooth_off_shows_cant_reach_your_phone_and_Try_again_reconnects()
    {
        await using var f = await OnboardingFixture.CreateAsync();
        f.H.SetOpenFailure(ErrorKind.PhoneUnreachable);
        await f.GoToConnectStepAsync();

        f.Vm.IsConnectErrorVisible.Should().BeTrue();
        f.Vm.ConnectError!.Title.Should().Be("Can't reach your phone");

        f.H.SetOpenResult(OpenResult.Ok());
        await f.Vm.TryAgainCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.H.OpenCalls.Should().Be(2);
        f.Vm.IsConnectErrorVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Muted_phone_audio_shows_an_unmute_hint()
    {
        await using var f = await OnboardingFixture.CreateAsync(AppSettings.Default with { PhoneMuted = true });
        await f.GoToConnectStepAsync();
        f.H.RaiseReceiver(ReceiverState.Opened);
        await f.SettleAsync();

        f.Vm.IsMutedHintVisible.Should().BeTrue();

        await f.Vm.UnmuteCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.PhoneAudio.Muted.Should().BeFalse();
        f.Vm.IsMutedHintVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Back_from_the_connect_step_stops_the_help_timer()
    {
        await using var f = await OnboardingFixture.CreateAsync();
        await f.GoToConnectStepAsync();

        f.Vm.BackCommand.Execute(null);
        await f.AdvanceAsync(TimeSpan.FromSeconds(61));

        f.Vm.Step.Should().Be(OnboardingStep.PickPhone);
        f.Vm.IsNoAudioHelpVisible.Should().BeFalse();
    }
}

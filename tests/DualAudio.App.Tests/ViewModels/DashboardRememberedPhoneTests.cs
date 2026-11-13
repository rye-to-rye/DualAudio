using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>Saved phone first, nicknames, "Forget in DualAudio", calm "Phone not found", auto-connect toggle.</summary>
public sealed class DashboardRememberedPhoneTests
{
    [Fact]
    public async Task Saved_phone_is_listed_first_under_its_nickname()
    {
        var settings = (AppSettings.Default with { PreferredDeviceId = OtherPhoneId }).WithNickname(OtherPhoneId, "Work phone");
        await using var f = new DashboardFixture(settings);
        await f.H.AddDeviceAsync(PhoneId, "Alpha");
        await f.H.AddDeviceAsync(OtherPhoneId, "Zulu");
        await f.Vm.InitializeAsync();

        f.Vm.Devices.Select(d => d.Name).Should().Equal("Work phone", "Alpha");
        f.Vm.Devices[0].IsSaved.Should().BeTrue();
        f.Vm.Devices[0].DisplayName.Should().Be("Work phone (saved)");
        f.Vm.SelectedDevice!.Id.Should().Be(OtherPhoneId);
    }

    [Fact]
    public async Task Nickname_survives_a_settings_round_trip_into_a_new_dashboard()
    {
        var store = new Core.Tests.Fakes.InMemorySettingsStore((AppSettings.Default with { PreferredDeviceId = PhoneId }).WithNickname(PhoneId, "Mine"));
        var reloaded = await store.LoadAsync(CancellationToken.None);
        await using var f = await DashboardFixture.CreateAsync(reloaded);

        f.Vm.Devices.Single().Name.Should().Be("Mine");
    }

    [Fact]
    public async Task Forget_removes_the_phone_from_DualAudio_settings_only()
    {
        var settings = (AppSettings.Default with { PreferredDeviceId = PhoneId, PhoneVolume = 30 }).WithNickname(PhoneId, "Mine");
        await using var f = await DashboardFixture.CreateAsync(settings);
        await f.H.ConnectFullyAsync();
        await f.SettleAsync();
        f.Vm.ForgetDeviceCommand.CanExecute(null).Should().BeTrue();

        await f.Vm.ForgetDeviceCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.Store.Current.PreferredDeviceId.Should().BeNull();
        f.Store.Current.DeviceNicknames.Should().BeEmpty();
        f.Store.Current.PhoneVolume.Should().Be(30);
        f.Vm.Devices.Single().Should().Be(new DeviceItem(PhoneId, PhoneName), "still listed: it is still paired in Windows");
        f.H.Sut.CurrentState.Should().Be(ConnectionState.Connected, "forgetting doesn't disconnect");
        f.H.CloseCalls.Should().Be(0);
        f.Vm.ForgetDeviceCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Forget_is_only_offered_for_the_saved_phone()
    {
        await using var f = await DashboardFixture.CreateAsync();

        f.Vm.ForgetDeviceCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Gave_up_is_a_calm_Phone_not_found_state_with_Retry_not_an_error_banner()
    {
        await using var f = await DashboardFixture.CreateAsync(AppSettings.Default with { PreferredDeviceId = PhoneId });
        f.H.SetOpenFailure(ErrorKind.PhoneUnreachable);
        await f.H.Sut.AutoConnectAsync(PhoneId);
        await f.SettleAsync();

        f.Vm.IsErrorVisible.Should().BeTrue("while retrying, the friendly banner explains it");
        f.Vm.Error!.Title.Should().Be("Can't reach your phone");
        f.Vm.Error.Message.Should().Contain("retrying automatically");
        f.Vm.StatusText.Should().Be("Reconnecting…");

        for (var i = 0; i < 10; i++)
        {
            await f.AdvanceAsync(TimeSpan.FromSeconds(30));
        }

        f.H.Sut.GaveUp.Should().BeTrue();
        f.Vm.IsErrorVisible.Should().BeFalse();
        f.Vm.IsPhoneNotFound.Should().BeTrue();
        f.Vm.StatusKind.Should().Be(StatusKind.Disconnected);
        f.Vm.StatusText.Should().Be("Phone not found");
        f.Vm.PhoneNotFoundMessage.Should().Contain(PhoneName);
        var opens = f.H.OpenCalls;

        f.H.SetOpenResult(OpenResult.Ok());
        await f.Vm.RetryFindPhoneCommand.ExecuteAsync(null);
        await f.SettleAsync();

        f.H.OpenCalls.Should().Be(opens + 1);
        f.Vm.IsPhoneNotFound.Should().BeFalse();
        f.Vm.ConnectionState.Should().Be(ConnectionState.Opening);
    }

    [Fact]
    public async Task Tray_tooltip_follows_connection_audio_mute_and_volume_with_the_nickname()
    {
        var settings = (AppSettings.Default with { PreferredDeviceId = PhoneId, PhoneVolume = 40 }).WithNickname(PhoneId, "Mine");
        await using var f = await DashboardFixture.CreateAsync(settings);
        f.Vm.TrayToolTip.Should().Be("DualAudio — Disconnected");

        await f.H.ConnectFullyAsync();
        await f.SettleAsync();
        f.Vm.TrayToolTip.Should().Be("DualAudio — Connected to Mine\nPhone volume 40%");

        f.H.SetPhoneSession(active: true, peak: 0.2f);
        await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));
        f.Vm.TrayToolTip.Should().StartWith("DualAudio — Receiving audio");

        await f.PhoneAudio.SetMutedAsync(true); // e.g. the Ctrl+Alt+M hotkey
        await f.SettleAsync();
        f.Vm.TrayToolTip.Should().StartWith("DualAudio — Phone muted");

        await f.PhoneAudio.SetVolumeAsync(45); // e.g. Ctrl+Alt+PgUp
        await f.SettleAsync();
        f.Vm.TrayToolTip.Should().EndWith("Phone volume 45%");
    }

    [Fact]
    public async Task Reload_picks_up_a_nickname_changed_on_the_settings_page()
    {
        await using var f = await DashboardFixture.CreateAsync(AppSettings.Default with { PreferredDeviceId = PhoneId });
        await f.Store.SaveAsync(f.Store.Current.WithNickname(PhoneId, "Renamed"), CancellationToken.None);

        await f.Vm.ReloadSettingsAsync();

        f.Vm.Devices.Single().DisplayName.Should().Be("Renamed (saved)");
    }

    [Fact]
    public async Task Connecting_while_onboarding_runs_does_not_save_the_phone_from_the_dashboard()
    {
        // The dashboard exists (subscribed) during onboarding but isn't initialized/shown yet.
        await using var f = new DashboardFixture();
        await f.H.ConnectFullyAsync();
        await f.SettleAsync();

        f.Store.Current.PreferredDeviceId.Should().BeNull("only onboarding saves it, once audio arrives");
    }
}

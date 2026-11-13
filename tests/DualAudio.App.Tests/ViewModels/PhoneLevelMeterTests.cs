using DualAudio.Core.Abstractions;
using DualAudio.Core.Hotkeys;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>The phone level meters (dashboard + flyout) and the mute shortcut hint.</summary>
public sealed class PhoneLevelMeterTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task Visible_meter_follows_the_phone_level()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Vm.DashboardLevel.SetVisible(true);
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.5f);
        await f.AdvanceAsync(Poll);

        f.Vm.DashboardLevel.Level.Should().BeApproximately(0.5, 1e-6);

        f.H.SetPhoneSession(active: true, peak: 0.2f);
        await f.AdvanceAsync(Poll);

        f.Vm.DashboardLevel.Level.Should().BeApproximately(0.2, 1e-6);
    }

    [Fact]
    public async Task Hidden_meters_get_no_updates_and_nothing_is_posted_to_the_UI_thread()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.05f);
        await f.AdvanceAsync(Poll); // receiving already true, so the loop below raises no Changed (which posts too)
        f.Dispatcher.QueueWork = true; // count posts instead of running them

        var changes = 0;
        f.Vm.DashboardLevel.PropertyChanged += (_, _) => changes++;
        f.Vm.FlyoutLevel.PropertyChanged += (_, _) => changes++;
        for (var i = 1; i <= 8; i++)
        {
            f.H.SetPhoneSession(active: true, peak: i / 10f);
            await f.AdvanceAsync(Poll);
        }

        f.Dispatcher.Pending.Should().Be(0, "level events are dropped before reaching the dispatcher while both windows are hidden");
        changes.Should().Be(0);
        f.Vm.DashboardLevel.Level.Should().Be(0);
        f.Vm.FlyoutLevel.Level.Should().Be(0);
    }

    [Fact]
    public async Task Showing_a_window_jumps_to_the_current_level_and_hiding_freezes_it()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.6f);
        await f.AdvanceAsync(Poll);

        f.Vm.FlyoutLevel.SetVisible(true);
        f.Vm.FlyoutLevel.Level.Should().BeApproximately(0.6, 1e-6);
        f.Vm.DashboardLevel.Level.Should().Be(0, "only the visible window's meter is updated");

        f.Vm.FlyoutLevel.SetVisible(false);
        f.H.SetPhoneSession(active: true, peak: 0.1f);
        await f.AdvanceAsync(Poll);

        f.Vm.FlyoutLevel.Level.Should().BeApproximately(0.6, 1e-6, "no updates while hidden");
    }

    [Fact]
    public async Task Level_posts_are_coalesced_while_the_UI_thread_is_busy()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Vm.DashboardLevel.SetVisible(true);
        await f.H.ConnectFullyAsync();
        await f.SettleAsync();
        f.Dispatcher.QueueWork = true;

        for (var i = 1; i <= 4; i++)
        {
            f.H.SetPhoneSession(active: true, peak: i / 10f);
            await f.AdvanceAsync(Poll);
        }

        f.Dispatcher.Pending.Should().BeLessThanOrEqualTo(2, "one level refresh at a time (plus possibly one Changed)");
        f.Dispatcher.Pump();
        f.Vm.DashboardLevel.Level.Should().BeApproximately(0.4, 1e-6, "the refresh reads the newest level");
    }

    [Fact]
    public async Task Disconnected_means_all_bars_empty()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Vm.DashboardLevel.SetVisible(true);
        f.Vm.FlyoutLevel.SetVisible(true);
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.7f);
        await f.AdvanceAsync(Poll);
        f.Vm.DashboardLevel.Level.Should().BeGreaterThan(0);

        await f.H.Sut.DisconnectAsync();
        await f.SettleAsync();

        f.Vm.DashboardLevel.Level.Should().Be(0);
        f.Vm.FlyoutLevel.Level.Should().Be(0);
    }

    [Fact]
    public async Task Muted_keeps_showing_the_level()
    {
        await using var f = await DashboardFixture.CreateAsync();
        f.Vm.DashboardLevel.SetVisible(true);
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.3f);
        await f.AdvanceAsync(Poll);

        f.Vm.IsMuted = true;
        f.H.SetPhoneSession(active: false, peak: 0.3f, muted: true);
        await f.AdvanceAsync(Poll);

        f.Vm.IsMuted.Should().BeTrue();
        f.Vm.DashboardLevel.Level.Should().BeApproximately(0.3, 1e-6, "the view dims it; the value keeps showing audio arriving");
    }

    // ------------------------------------------------------------------ mute shortcut hint

    [Fact]
    public async Task Mute_shortcut_comes_from_the_hotkey_settings_and_follows_changes()
    {
        await using var f = new DashboardFixture(withHotkeys: true);
        f.Vm.HasMuteShortcut.Should().BeFalse("nothing registered yet");

        await f.Hotkeys!.ApplyFromSettingsAsync();
        f.Vm.MuteShortcut.Should().Be("Ctrl+Alt+M");
        f.Vm.MuteShortcutKeys.Should().Equal("Ctrl", "Alt", "M");

        var changed = await f.Hotkeys.ChangeAsync(new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Shift, 'K'));
        changed.Should().Be(HotkeyChangeResult.Changed);

        f.Vm.MuteShortcut.Should().Be("Ctrl+Shift+K");
        f.Vm.MuteShortcutKeys.Should().Equal("Ctrl", "Shift", "K");
    }

    [Fact]
    public async Task No_mute_shortcut_hint_when_the_shortcut_is_taken_by_another_app()
    {
        await using var f = new DashboardFixture(withHotkeys: true);
        f.HotkeyService.TakenByOtherApps.Add(new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt, 'M'));

        await f.Hotkeys!.ApplyFromSettingsAsync();

        f.Vm.HasMuteShortcut.Should().BeFalse("pressing it would do nothing");
        f.Vm.MuteShortcutKeys.Should().BeEmpty();
    }
}

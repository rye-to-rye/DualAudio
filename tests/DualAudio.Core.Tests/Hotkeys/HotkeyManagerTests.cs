using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.Core.Tests.Hotkeys;

public sealed class HotkeyManagerTests : IAsyncLifetime
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private static readonly HotkeyBinding CtrlAltM = new(HotkeyAction.ToggleMute, CtrlAlt, 0x4D);
    private static readonly HotkeyBinding CtrlAltU = new(HotkeyAction.ToggleMute, CtrlAlt, 0x55);

    private readonly CoordinatorHarness _h = new();
    private readonly FakeHotkeyService _registrar = new();
    private InMemorySettingsStore _store = new();
    private PhoneAudioService _phoneAudio = null!;
    private HotkeyManager _sut = null!;

    public async Task InitializeAsync() => await CreateAsync(AppSettings.Default);

    public async Task DisposeAsync()
    {
        _sut.Dispose();
        await _phoneAudio.DisposeAsync();
        await _h.DisposeAsync();
    }

    [Fact]
    public async Task Start_registers_the_three_default_shortcuts()
    {
        await _sut.ApplyFromSettingsAsync();

        _registrar.Registered.Values.Should().BeEquivalentTo(AppSettings.DefaultHotkeys);
        _sut.Current.Values.Should().OnlyContain(s => s.IsRegistered);
    }

    [Fact]
    public async Task A_shortcut_taken_at_start_is_reported_and_the_others_still_work()
    {
        _registrar.TakenByOtherApps.Add(CtrlAltM);

        await _sut.ApplyFromSettingsAsync();

        _sut.Current[HotkeyAction.ToggleMute].IsRegistered.Should().BeFalse();
        _sut.Current[HotkeyAction.VolumeUp].IsRegistered.Should().BeTrue();
    }

    [Fact]
    public async Task Change_replaces_the_old_combination_and_is_saved()
    {
        await _sut.ApplyFromSettingsAsync();

        (await _sut.ChangeAsync(CtrlAltU)).Should().Be(HotkeyChangeResult.Changed);

        _registrar.Registered[HotkeyAction.ToggleMute].Should().Be(CtrlAltU, "the old Ctrl+Alt+M no longer fires");
        _store.Current.HotkeyFor(HotkeyAction.ToggleMute).Should().Be(CtrlAltU);
        _store.Current.HotkeyFor(HotkeyAction.VolumeUp).Should().Be(AppSettings.DefaultHotkeys[1]);
    }

    [Fact]
    public async Task Combination_in_use_by_another_app_keeps_the_previous_binding()
    {
        await _sut.ApplyFromSettingsAsync();
        _registrar.TakenByOtherApps.Add(CtrlAltU);
        var saves = _store.SaveCount;

        (await _sut.ChangeAsync(CtrlAltU)).Should().Be(HotkeyChangeResult.InUseByAnotherApp);

        _registrar.Registered[HotkeyAction.ToggleMute].Should().Be(CtrlAltM, "re-registered");
        _sut.Current[HotkeyAction.ToggleMute].Should().Be(new HotkeyState(CtrlAltM, true));
        _store.SaveCount.Should().Be(saves, "nothing is saved");
    }

    [Fact]
    public async Task Combination_of_another_DualAudio_shortcut_is_refused()
    {
        await _sut.ApplyFromSettingsAsync();

        (await _sut.ChangeAsync(CtrlAltM with { Action = HotkeyAction.VolumeUp })).Should().Be(HotkeyChangeResult.AlreadyUsedInDualAudio);

        _registrar.Registered[HotkeyAction.VolumeUp].Should().Be(AppSettings.DefaultHotkeys[1]);
    }

    [Fact]
    public async Task Invalid_and_unchanged_combinations()
    {
        await _sut.ApplyFromSettingsAsync();

        (await _sut.ChangeAsync(new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.None, 0x4D))).Should().Be(HotkeyChangeResult.Invalid);
        (await _sut.ChangeAsync(new HotkeyBinding(HotkeyAction.ToggleConnection, CtrlAlt, 0x43))).Should().Be(HotkeyChangeResult.Invalid, "not offered");
        (await _sut.ChangeAsync(CtrlAltM)).Should().Be(HotkeyChangeResult.Unchanged);
    }

    [Fact]
    public async Task Taken_shortcut_can_be_moved_to_a_free_combination()
    {
        _registrar.TakenByOtherApps.Add(CtrlAltM);
        await _sut.ApplyFromSettingsAsync();

        (await _sut.ChangeAsync(CtrlAltU)).Should().Be(HotkeyChangeResult.Changed);

        _sut.Current[HotkeyAction.ToggleMute].IsRegistered.Should().BeTrue();
    }

    [Fact]
    public async Task Saved_custom_bindings_are_used_at_start()
    {
        await RecreateAsync(AppSettings.Default.WithHotkey(CtrlAltU));

        await _sut.ApplyFromSettingsAsync();

        _registrar.Registered[HotkeyAction.ToggleMute].Should().Be(CtrlAltU);
    }

    [Fact]
    public async Task Mute_hotkey_toggles_phone_mute_only()
    {
        await _sut.ApplyFromSettingsAsync();

        _registrar.Press(HotkeyAction.ToggleMute);
        await _sut.LastAction;
        _phoneAudio.Muted.Should().BeTrue();
        _store.Current.PhoneMuted.Should().BeTrue();

        _registrar.Press(HotkeyAction.ToggleMute);
        await _sut.LastAction;
        _phoneAudio.Muted.Should().BeFalse();
        await _h.Sessions.Received().SetPhoneMutedAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Volume_hotkeys_step_by_5_and_stay_within_0_to_100()
    {
        await RecreateAsync(AppSettings.Default with { PhoneVolume = 97 });
        await _sut.ApplyFromSettingsAsync();

        _registrar.Press(HotkeyAction.VolumeUp);
        await _sut.LastAction;
        _phoneAudio.VolumePercent.Should().Be(100);

        _registrar.Press(HotkeyAction.VolumeDown);
        await _sut.LastAction;
        _phoneAudio.VolumePercent.Should().Be(95);

        await _phoneAudio.SetVolumeAsync(3);
        _registrar.Press(HotkeyAction.VolumeDown);
        await _sut.LastAction;
        _phoneAudio.VolumePercent.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_unregisters_everything()
    {
        await _sut.ApplyFromSettingsAsync();

        _sut.Dispose();

        _registrar.Registered.Should().BeEmpty();
    }

    private async Task CreateAsync(AppSettings settings)
    {
        _store = new InMemorySettingsStore(settings);
        _phoneAudio = new PhoneAudioService(_h.Sessions, _h.Sut, _store, _h.Clock);
        await _phoneAudio.InitializeAsync();
        _sut = new HotkeyManager(_registrar, _phoneAudio, _store);
    }

    private async Task RecreateAsync(AppSettings settings)
    {
        _sut.Dispose();
        await _phoneAudio.DisposeAsync();
        await CreateAsync(settings);
    }
}

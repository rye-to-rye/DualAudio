using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Settings;
using DualAudio.Core.Startup;
using DualAudio.Core.Tests.Fakes;
using DualAudio.Infrastructure;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IAsyncLifetime
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private const string Exe = @"C:\Apps\DualAudio\DualAudio.exe";

    private readonly CoordinatorHarness _h = new();
    private readonly FakeHotkeyService _registrar = new();
    private readonly FakeAutoStartEntry _runKey = new();
    private readonly ISystemSettingsLauncher _launcher = Substitute.For<ISystemSettingsLauncher>();
    private readonly IConfirmDialog _confirm = Substitute.For<IConfirmDialog>();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local\DualAudio");
    private InMemorySettingsStore _store = null!;
    private PhoneAudioService _phoneAudio = null!;
    private HotkeyManager _hotkeys = null!;
    private SettingsViewModel _sut = null!;

    public Task InitializeAsync() => CreateAsync((AppSettings.Default with { PreferredDeviceId = PhoneId }).WithNickname(PhoneId, "Mine"));

    public async Task DisposeAsync()
    {
        _hotkeys.Dispose();
        await _phoneAudio.DisposeAsync();
        await _h.DisposeAsync();
    }

    [Fact]
    public void Shows_the_saved_values()
    {
        _sut.AutoConnectOnStart.Should().BeTrue();
        _sut.StartWithWindows.Should().BeFalse();
        _sut.SavedPhoneName.Should().Be(PhoneName);
        _sut.Nickname.Should().Be("Mine");
        _sut.Hotkeys.Select(r => r.Keys).Should().Equal("Ctrl+Alt+M", "Ctrl+Alt+PgUp", "Ctrl+Alt+PgDn");
        _sut.Hotkeys.Should().OnlyContain(r => r.Message == null);
        _sut.Licenses.Should().Contain(l => l.Package == "H.NotifyIcon.Wpf" && l.License == "MIT");
        _sut.Version.Should().NotBeNullOrWhiteSpace();
        _store.SaveCount.Should().Be(0, "loading writes nothing back");
    }

    [Fact]
    public async Task Auto_connect_toggle_is_saved_and_other_settings_kept()
    {
        _sut.AutoConnectOnStart = false;
        await _sut.Save;

        _store.Current.AutoConnectOnStart.Should().BeFalse();
        _store.Current.NicknameFor(PhoneId).Should().Be("Mine");
    }

    [Fact]
    public async Task Start_with_Windows_writes_and_removes_the_run_entry()
    {
        _sut.StartWithWindows = true;
        await _sut.Save;

        _store.Current.StartWithWindows.Should().BeTrue();
        _runKey.Value.Should().Be("\"C:\\Apps\\DualAudio\\DualAudio.exe\" --minimized");

        _sut.StartWithWindows = false;
        await _sut.Save;

        _runKey.Value.Should().BeNull();
        _sut.StartWithWindowsMessage.Should().BeNull();
    }

    [Fact]
    public async Task Start_with_Windows_failure_is_shown()
    {
        _runKey.Throw = true;

        _sut.StartWithWindows = true;
        await _sut.Save;

        _sut.StartWithWindowsMessage.Should().Be(SettingsViewModel.StartupFailedMessage);
    }

    [Fact]
    public async Task Recording_a_free_combination_changes_the_shortcut()
    {
        var row = _sut.Hotkeys[0];
        _sut.BeginRecordingCommand.Execute(row);
        row.IsRecording.Should().BeTrue();
        row.Keys.Should().Be(SettingsViewModel.RecordingText);

        var result = await _sut.CompleteRecordingAsync(CtrlAlt, 0x55);

        result.Should().Be(HotkeyChangeResult.Changed);
        row.Keys.Should().Be("Ctrl+Alt+U");
        row.Message.Should().BeNull();
        row.IsRecording.Should().BeFalse();
        _registrar.Registered[HotkeyAction.ToggleMute].VirtualKey.Should().Be(0x55);
        _store.Current.HotkeyFor(HotkeyAction.ToggleMute)!.VirtualKey.Should().Be(0x55);
    }

    [Theory]
    [InlineData(0x55, "Ctrl+Alt+U", null)] // changed
    [InlineData(0x4D, "Ctrl+Alt+M", null)] // unchanged (same combination)
    [InlineData(0x22, "Ctrl+Alt+M", SettingsViewModel.DuplicateMessage)] // Ctrl+Alt+PgDn: another DualAudio shortcut
    public async Task Recorder_shows_Press_keys_then_the_final_combination_once_with_no_flash(int virtualKey, string finalKeys, string? message)
    {
        var row = _sut.Hotkeys[0];
        _sut.BeginRecordingCommand.Execute(row);
        row.Keys.Should().Be("Press keys…");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add($"{e.PropertyName}={(e.PropertyName == nameof(HotkeyRow.IsRecording) ? row.IsRecording : row.Keys)}|{row.Keys}");

        await _sut.CompleteRecordingAsync(CtrlAlt, virtualKey);

        // The button text goes straight from the prompt to the final combination, and only then leaves the recording
        // look; the Changed refresh during the change must not touch the row.
        seen.Where(s => s.StartsWith("Keys=", StringComparison.Ordinal)).Should().Equal($"Keys={finalKeys}|{finalKeys}");
        seen.Should().ContainSingle(s => s.StartsWith("IsRecording=", StringComparison.Ordinal))
            .Which.Should().Be($"IsRecording=False|{finalKeys}");
        seen.FindIndex(s => s.StartsWith("Keys=", StringComparison.Ordinal))
            .Should().BeLessThan(seen.FindIndex(s => s.StartsWith("IsRecording=", StringComparison.Ordinal)));
        row.Message.Should().Be(message);
    }

    [Fact]
    public async Task Combination_used_by_another_app_shows_in_use_and_keeps_the_previous_binding()
    {
        _registrar.TakenByOtherApps.Add(new HotkeyBinding(HotkeyAction.VolumeUp, CtrlAlt, 0x26));
        var row = _sut.Hotkeys[1];
        _sut.BeginRecordingCommand.Execute(row);

        var result = await _sut.CompleteRecordingAsync(CtrlAlt, 0x26);

        result.Should().Be(HotkeyChangeResult.InUseByAnotherApp);
        row.Message.Should().Be("Shortcut in use by another app");
        row.Keys.Should().Be("Ctrl+Alt+PgUp", "the previous binding is kept");
        _registrar.Registered[HotkeyAction.VolumeUp].Should().Be(AppSettings.DefaultHotkeys[1]);
    }

    [Fact]
    public async Task Duplicate_and_invalid_combinations_explain_why()
    {
        _sut.BeginRecordingCommand.Execute(_sut.Hotkeys[1]);
        (await _sut.CompleteRecordingAsync(CtrlAlt, 0x4D)).Should().Be(HotkeyChangeResult.AlreadyUsedInDualAudio);
        _sut.Hotkeys[1].Message.Should().Be(SettingsViewModel.DuplicateMessage);

        _sut.BeginRecordingCommand.Execute(_sut.Hotkeys[2]);
        (await _sut.CompleteRecordingAsync(HotkeyModifiers.Shift, 0x41)).Should().Be(HotkeyChangeResult.Invalid);
        _sut.Hotkeys[2].Message.Should().Be(SettingsViewModel.InvalidMessage);
        _sut.Hotkeys[2].Keys.Should().Be("Ctrl+Alt+PgDn");
    }

    [Fact]
    public async Task Shortcut_taken_at_start_is_flagged_on_the_page()
    {
        _registrar.TakenByOtherApps.Add(AppSettings.DefaultHotkeys[0]);
        await _hotkeys.ApplyFromSettingsAsync();

        _sut.Hotkeys[0].Message.Should().Be(SettingsViewModel.InUseMessage);
    }

    [Fact]
    public async Task Cancel_recording_restores_the_text()
    {
        _sut.BeginRecordingCommand.Execute(_sut.Hotkeys[0]);

        _sut.CancelRecordingCommand.Execute(null);

        _sut.Hotkeys[0].Keys.Should().Be("Ctrl+Alt+M");
        (await _sut.CompleteRecordingAsync(CtrlAlt, 0x55)).Should().BeNull("nothing is recording");
    }

    [Fact]
    public async Task Nickname_is_saved_trimmed_and_announced()
    {
        var changed = 0;
        _sut.SettingsChanged += (_, _) => changed++;
        _sut.Nickname = "  Work phone ";

        await _sut.SaveNicknameCommand.ExecuteAsync(null);

        _store.Current.NicknameFor(PhoneId).Should().Be("Work phone");
        _sut.Nickname.Should().Be("Work phone");
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Nickname_needs_a_saved_phone()
    {
        await RecreateAsync(AppSettings.Default);

        _sut.HasSavedPhone.Should().BeFalse();
        _sut.SaveNicknameCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Open_log_folder()
    {
        await _sut.OpenLogFolderCommand.ExecuteAsync(null);

        await _launcher.Received(1).OpenFolderAsync(_paths.LogDirectory);
    }

    [Fact]
    public async Task Reset_does_nothing_without_confirmation()
    {
        _confirm.Confirm(default!, default!).ReturnsForAnyArgs(false);

        await _sut.ResetSettingsCommand.ExecuteAsync(null);

        _store.Current.PreferredDeviceId.Should().Be(PhoneId);
        _store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Confirmed_reset_restores_defaults_everywhere()
    {
        _sut.StartWithWindows = true;
        await _sut.Save;
        _sut.BeginRecordingCommand.Execute(_sut.Hotkeys[0]);
        await _sut.CompleteRecordingAsync(CtrlAlt, 0x55);
        await _phoneAudio.SetVolumeAsync(30);
        await _phoneAudio.SetMutedAsync(true);
        _confirm.Confirm(default!, default!).ReturnsForAnyArgs(true);
        var changed = 0;
        _sut.SettingsChanged += (_, _) => changed++;

        await _sut.ResetSettingsCommand.ExecuteAsync(null);

        _store.Current.Should().BeEquivalentTo(AppSettings.Default);
        _phoneAudio.VolumePercent.Should().Be(100);
        _phoneAudio.Muted.Should().BeFalse();
        _runKey.Value.Should().BeNull();
        _registrar.Registered[HotkeyAction.ToggleMute].Should().Be(AppSettings.DefaultHotkeys[0]);
        _sut.Hotkeys[0].Keys.Should().Be("Ctrl+Alt+M");
        _sut.HasSavedPhone.Should().BeFalse();
        changed.Should().Be(1);
    }

    private async Task CreateAsync(AppSettings settings)
    {
        _store = new InMemorySettingsStore(settings);
        _phoneAudio = new PhoneAudioService(_h.Sessions, _h.Sut, _store, _h.Clock);
        await _phoneAudio.InitializeAsync();
        await _h.AddDeviceAsync();
        _hotkeys = new HotkeyManager(_registrar, _phoneAudio, _store);
        await _hotkeys.ApplyFromSettingsAsync();
        _sut = new SettingsViewModel(
            _store, _hotkeys, new AutoStartRegistration(_runKey), _h.Sut, _phoneAudio, _launcher, _confirm, new TestDispatcher(), _paths)
        {
            ExePath = () => Exe,
        };
        await _sut.InitializeAsync();
    }

    private async Task RecreateAsync(AppSettings settings)
    {
        _sut.Detach();
        _hotkeys.Dispose();
        await _phoneAudio.DisposeAsync();
        await CreateAsync(settings);
    }
}

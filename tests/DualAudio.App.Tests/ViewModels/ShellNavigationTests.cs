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

/// <summary>Settings is a page inside the main window: dashboard -> settings -> back, never a second window.</summary>
public sealed class ShellNavigationTests : IAsyncLifetime
{
    private readonly CoordinatorHarness _h = new();
    private readonly FakeHotkeyService _registrar = new();
    private readonly IAppShell _shell = Substitute.For<IAppShell>();
    private readonly List<SettingsViewModel> _pages = [];
    private InMemorySettingsStore _store = null!;
    private PhoneAudioService _phoneAudio = null!;
    private HotkeyManager _hotkeys = null!;
    private ShellViewModel _sut = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemorySettingsStore((AppSettings.Default with { PreferredDeviceId = PhoneId, TrayNoticeShown = true }).WithNickname(PhoneId, "Mine"));
        _phoneAudio = new PhoneAudioService(_h.Sessions, _h.Sut, _store, _h.Clock);
        await _phoneAudio.InitializeAsync();
        await _h.AddDeviceAsync();
        _hotkeys = new HotkeyManager(_registrar, _phoneAudio, _store);
        await _hotkeys.ApplyFromSettingsAsync();
        _sut = new ShellViewModel(_store, _shell, settingsPageFactory: CreatePage);
        await _sut.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _hotkeys.Dispose();
        await _phoneAudio.DisposeAsync();
        await _h.DisposeAsync();
    }

    [Fact]
    public void Starts_on_the_dashboard()
    {
        _sut.CurrentPage.Should().Be(ShellPage.Dashboard);
        _sut.IsDashboardPage.Should().BeTrue();
        _sut.SettingsPage.Should().BeNull();
        _sut.GoBackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Settings_opens_as_a_page_in_the_main_window_and_back_returns_to_the_dashboard()
    {
        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;

        _shell.Received(1).ShowMainWindow();
        _sut.CurrentPage.Should().Be(ShellPage.Settings);
        _sut.IsSettingsPage.Should().BeTrue();
        _sut.SettingsPage.Should().BeSameAs(_pages.Single());
        _sut.SettingsPage!.Nickname.Should().Be("Mine", "the page is loaded");
        _sut.GoBackCommand.CanExecute(null).Should().BeTrue();

        _sut.GoBackCommand.Execute(null);

        _sut.CurrentPage.Should().Be(ShellPage.Dashboard);
        _sut.SettingsPage.Should().BeNull();
        _sut.GoBackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Settings_again_while_open_keeps_the_same_page()
    {
        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;
        _sut.OpenSettingsCommand.Execute(null);

        _pages.Should().HaveCount(1);
        _shell.Received(2).ShowMainWindow();
    }

    [Fact]
    public async Task Leaving_the_page_cancels_a_recording_and_stops_listening_for_shortcut_changes()
    {
        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;
        var page = _sut.SettingsPage!;
        page.BeginRecordingCommand.Execute(page.Hotkeys[0]);

        _sut.GoBackCommand.Execute(null);

        page.Recording.Should().BeNull();
        page.Hotkeys[0].IsRecording.Should().BeFalse();
        page.Hotkeys[0].Keys.Should().Be("Ctrl+Alt+M");

        // Detached: a later change doesn't touch the old page.
        await _hotkeys.ChangeAsync(new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x55));
        page.Hotkeys[0].Keys.Should().Be("Ctrl+Alt+M");
    }

    [Fact]
    public async Task Open_DualAudio_and_closing_the_window_return_to_the_dashboard()
    {
        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;
        _sut.OpenMainCommand.Execute(null);
        _sut.CurrentPage.Should().Be(ShellPage.Dashboard);

        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;
        _sut.OnMainWindowClosing().Should().Be(CloseDecision.HideToTray);
        _sut.CurrentPage.Should().Be(ShellPage.Dashboard);
        _pages.Should().HaveCount(2, "a fresh page each time it opens");
    }

    [Fact]
    public async Task Settings_changes_on_the_page_are_forwarded_for_the_dashboard()
    {
        var changed = 0;
        _sut.SettingsChanged += (_, _) => changed++;
        _sut.OpenSettingsCommand.Execute(null);
        await _sut.SettingsLoad;

        _sut.SettingsPage!.Nickname = "Work";
        await _sut.SettingsPage.SaveNicknameCommand.ExecuteAsync(null);
        changed.Should().Be(1);

        var page = _sut.SettingsPage;
        _sut.GoBackCommand.Execute(null);
        await page.SaveNicknameCommand.ExecuteAsync(null);
        changed.Should().Be(1, "unsubscribed when the page closed");
    }

    private SettingsViewModel CreatePage()
    {
        var page = new SettingsViewModel(
            _store,
            _hotkeys,
            new AutoStartRegistration(new FakeAutoStartEntry()),
            _h.Sut,
            _phoneAudio,
            Substitute.For<ISystemSettingsLauncher>(),
            Substitute.For<IConfirmDialog>(),
            new TestDispatcher(),
            new AppPaths(@"C:\Users\test\AppData\Local\DualAudio"));
        _pages.Add(page);
        return page;
    }
}

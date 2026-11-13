using DualAudio.App.ViewModels;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.App.Tests.ViewModels;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(ConnectionState.Connected, false, false, null, false, false, "DualAudio — Connected to Shlawg\nPhone volume 60%")]
    [InlineData(ConnectionState.Connected, false, false, null, true, false, "DualAudio — Receiving audio\nPhone volume 60%")]
    [InlineData(ConnectionState.Connected, false, false, null, true, true, "DualAudio — Phone muted\nPhone volume 60%")]
    [InlineData(ConnectionState.Disconnected, true, false, ErrorKind.PhoneUnreachable, false, false, "DualAudio — Reconnecting…")]
    [InlineData(ConnectionState.Disconnected, false, false, null, false, false, "DualAudio — Disconnected")]
    [InlineData(ConnectionState.Idle, false, false, null, false, false, "DualAudio — Disconnected")]
    [InlineData(ConnectionState.Opening, false, false, null, false, false, "DualAudio — Connecting…")]
    [InlineData(ConnectionState.WaitingForPhone, false, false, null, false, false, "DualAudio — Waiting for phone…")]
    [InlineData(ConnectionState.Error, false, false, ErrorKind.BluetoothOff, false, false, "DualAudio — Bluetooth is off")]
    [InlineData(ConnectionState.Error, false, false, ErrorKind.PhoneUnreachable, false, false, "DualAudio — Can't reach your phone")]
    [InlineData(ConnectionState.Error, false, true, ErrorKind.PhoneUnreachable, false, false, "DualAudio — Phone not found")]
    public void Tooltip_text(ConnectionState state, bool reconnecting, bool gaveUp, ErrorKind? error, bool receiving, bool muted, string expected)
    {
        TrayStatus.ToolTip(state, reconnecting, gaveUp, error, receiving, muted, "Shlawg", 60).Should().Be(expected);
    }

    [Fact]
    public void Unknown_name_and_long_names_stay_within_the_tray_limit()
    {
        TrayStatus.ToolTip(ConnectionState.Connected, false, false, null, false, false, null, 100)
            .Should().Be("DualAudio — Connected to your phone\nPhone volume 100%");
        TrayStatus.ToolTip(ConnectionState.Connected, false, false, null, false, false, new string('x', 300), 100)
            .Length.Should().BeLessThanOrEqualTo(TrayStatus.MaxLength);
    }
}

public sealed class ShellViewModelTests
{
    private readonly IAppShell _shell = Substitute.For<IAppShell>();

    [Fact]
    public async Task First_close_hides_to_tray_with_a_one_off_notice_that_is_remembered()
    {
        var store = new InMemorySettingsStore();
        var sut = await CreateAsync(store);

        sut.OnMainWindowClosing().Should().Be(CloseDecision.HideToTrayWithNotice);
        await sut.NoticeSave;
        sut.OnMainWindowClosing().Should().Be(CloseDecision.HideToTray, "the notice is shown only once");

        store.Current.TrayNoticeShown.Should().BeTrue();
        var nextStart = await CreateAsync(store);
        nextStart.OnMainWindowClosing().Should().Be(CloseDecision.HideToTray, "remembered across restarts");
        await _shell.DidNotReceive().ExitAsync();
    }

    [Fact]
    public async Task Exit_quits_through_the_shell_and_then_lets_the_window_close()
    {
        var sut = await CreateAsync(new InMemorySettingsStore(AppSettings.Default with { TrayNoticeShown = true }));

        await sut.ExitCommand.ExecuteAsync(null);
        await sut.ExitCommand.ExecuteAsync(null);

        await _shell.Received(1).ExitAsync();
        sut.IsExiting.Should().BeTrue();
        sut.OnMainWindowClosing().Should().Be(CloseDecision.Close);
    }

    [Fact]
    public async Task Open_and_Settings_show_the_main_window()
    {
        var sut = await CreateAsync(new InMemorySettingsStore());

        sut.OpenMainCommand.Execute(null);
        sut.OpenSettingsCommand.Execute(null);

        _shell.Received(2).ShowMainWindow();
        sut.CurrentPage.Should().Be(ShellPage.Settings);
    }

    private async Task<ShellViewModel> CreateAsync(InMemorySettingsStore store)
    {
        var sut = new ShellViewModel(store, _shell);
        await sut.InitializeAsync();
        return sut;
    }
}

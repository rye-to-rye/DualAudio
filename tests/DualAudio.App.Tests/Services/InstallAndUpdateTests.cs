using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Startup;
using DualAudio.Core.Tests.Fakes;
using DualAudio.Core.Updates;
using DualAudio.Platform.Windows.Updates;

namespace DualAudio.App.Tests.Services;

/// <summary>Prompt 10: Velopack install hooks, the Run value path, and the update banner / tray line.</summary>
public sealed class InstallAndUpdateTests
{
    private const string StableExe = @"C:\Users\test\AppData\Local\DualAudioApp\current\DualAudio.exe";

    [Fact]
    public void Run_value_uses_Velopacks_stable_exe_when_installed()
    {
        RunKeyPath.Choose(StableExe, @"C:\Somewhere\else\DualAudio.exe").Should().Be(StableExe);
        RunKeyPath.Choose(null, @"C:\dev\DualAudio\bin\DualAudio.exe").Should().Be(@"C:\dev\DualAudio\bin\DualAudio.exe", "dev build");

        var entry = new FakeAutoStartEntry();
        new AutoStartRegistration(entry).Sync(enabled: true, RunKeyPath.Choose(StableExe, null));
        entry.Value.Should().Be($"\"{StableExe}\" --minimized");
        entry.Value.Should().NotContain("app-").And.Contain(@"\current\", "a folder Velopack replaces in place on update");
    }

    [Fact]
    public void Pack_id_never_collides_with_the_settings_folder()
    {
        // Velopack deletes %LOCALAPPDATA%\<packId> on uninstall; settings and logs live in %LOCALAPPDATA%\DualAudio.
        VelopackUpdater.PackId.Should().Be("DualAudioApp");
        VelopackUpdater.PackId.Should().NotBeEquivalentTo("DualAudio");
        var settingsRoot = DualAudio.Infrastructure.AppPaths.ForCurrentUser().RootDirectory;
        Path.GetFileName(settingsRoot).Should().Be("DualAudio");
        Path.GetFileName(settingsRoot).Should().NotBeEquivalentTo(VelopackUpdater.PackId);
    }

    [Fact]
    public void Uninstall_hook_asks_the_running_app_to_exit_then_removes_the_Run_value()
    {
        var entry = new FakeAutoStartEntry { Value = $"\"{StableExe}\" --minimized" };
        var calls = new List<string>();

        InstallHooks.BeforeUninstall(entry, timeout =>
        {
            calls.Add($"exit {timeout.TotalSeconds}s");
            entry.Value.Should().NotBeNull("the app exits (tray icon removed) before the value is deleted");
            return true;
        });

        calls.Should().Equal("exit 10s");
        entry.Value.Should().BeNull();
    }

    [Fact]
    public void Uninstall_hook_never_throws_into_the_installer()
    {
        var entry = new FakeAutoStartEntry { Value = "x", Throw = true };

        var act = () => InstallHooks.BeforeUninstall(entry, _ => throw new InvalidOperationException("no instance"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Tray_tooltip_has_the_update_line_and_it_survives_the_length_cut()
    {
        TrayStatus.ToolTip(ConnectionState.Connected, false, false, null, true, false, "Shlawg", 60, updateReady: true)
            .Should().Be("DualAudio — Receiving audio\nUpdate ready: restart to install\nPhone volume 60%");
        TrayStatus.ToolTip(ConnectionState.Connected, false, false, null, false, false, new string('x', 80), 60, updateReady: true)
            .Should().Contain(TrayStatus.UpdateReadyLine);
        TrayStatus.ToolTip(ConnectionState.Idle, false, false, null, false, false, null, 100)
            .Should().NotContain("Update");
    }

    [Fact]
    public async Task Dashboard_banner_appears_when_ready_and_Later_hides_it_and_the_tray_line()
    {
        var updater = new FakeAppUpdater { Available = new AvailableUpdate("0.1.1") };
        var clock = new FakeClock();
        await using var updates = new UpdateService(updater, clock);
        await using var f = await ViewModels.DashboardFixture.CreateAsync(updates: updates);
        updates.Start();
        f.Vm.IsUpdateBannerVisible.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(60));
        await f.SettleAsync();

        f.Vm.IsUpdateBannerVisible.Should().BeTrue();
        f.Vm.UpdateBannerText.Should().Be("DualAudio 0.1.1 is ready. Restart to install it.");
        f.Vm.TrayToolTip.Should().Contain(TrayStatus.UpdateReadyLine);

        f.Vm.UpdateLaterCommand.Execute(null);
        await f.SettleAsync();
        f.Vm.IsUpdateBannerVisible.Should().BeFalse();
        f.Vm.TrayToolTip.Should().NotContain(TrayStatus.UpdateReadyLine);
        updater.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_now_on_the_banner_applies_with_restart()
    {
        var updater = new FakeAppUpdater { Available = new AvailableUpdate("0.1.1") };
        var clock = new FakeClock();
        await using var updates = new UpdateService(updater, clock);
        await using var f = await ViewModels.DashboardFixture.CreateAsync(updates: updates);
        var exitRequested = false;
        updates.RestartRequested += (_, _) => exitRequested = true;
        updates.Start();
        clock.Advance(TimeSpan.FromSeconds(60));
        await f.SettleAsync();

        f.Vm.RestartToUpdateCommand.Execute(null);

        exitRequested.Should().BeTrue();
        updater.Applied.Should().ContainSingle().Which.Restart.Should().BeTrue();
    }
}

using DualAudio.Core.Tests.Fakes;
using DualAudio.Core.Updates;

namespace DualAudio.Core.Tests.Updates;

/// <summary>Prompt 10: update checks and UI state with a fake source (no network). The user stays in control.</summary>
public sealed class UpdateServiceTests
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly AvailableUpdate V011 = new("0.1.1");

    private readonly FakeClock _clock = new();
    private readonly FakeAppUpdater _updater = new();

    [Fact]
    public async Task Not_installed_never_checks_not_even_manually()
    {
        _updater.IsInstalled = false;
        await using var sut = new UpdateService(_updater, _clock);

        sut.Start();
        _clock.Advance(TimeSpan.FromDays(3));
        await sut.CheckNowAsync();

        _updater.Checks.Should().Be(0);
        sut.Current.State.Should().Be(UpdateState.NotInstalled);
        UpdateService.Describe(sut.Current).Should().Be(UpdateService.NotInstalledText);
        sut.Current.IsBannerVisible.Should().BeFalse();
    }

    [Fact]
    public async Task First_check_60s_after_start_then_every_12h()
    {
        await using var sut = new UpdateService(_updater, _clock);
        sut.Start();

        _clock.Advance(FirstCheck - TimeSpan.FromSeconds(1));
        _updater.Checks.Should().Be(0, "start-up isn't slowed down");
        _clock.Advance(TimeSpan.FromSeconds(1));
        _updater.Checks.Should().Be(1);

        _clock.Advance(Interval - TimeSpan.FromSeconds(1));
        _updater.Checks.Should().Be(1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _updater.Checks.Should().Be(2);
        sut.Current.State.Should().Be(UpdateState.UpToDate);
        sut.Current.LastCheckAt.Should().NotBeNull();
        sut.Current.LastResult.Should().Be(UpdateService.UpToDateText);
    }

    [Fact]
    public async Task Banner_appears_only_after_the_download_completes()
    {
        _updater.Available = V011;
        _updater.DownloadGate = new TaskCompletionSource();
        await using var sut = new UpdateService(_updater, _clock);
        sut.Start();

        _clock.Advance(FirstCheck);
        sut.Current.State.Should().Be(UpdateState.Downloading);
        sut.Current.IsBannerVisible.Should().BeFalse("nothing to restart for yet");

        _updater.DownloadGate.SetResult();
        await WaitUntilAsync(() => sut.Current.State == UpdateState.Ready);
        sut.Current.IsBannerVisible.Should().BeTrue();
        sut.Current.ReadyVersion.Should().Be("0.1.1");
        _updater.Applied.Should().BeEmpty("never applied on its own");
    }

    [Fact]
    public async Task Later_hides_the_banner_until_the_next_start()
    {
        _updater.Available = V011;
        await using (var first = new UpdateService(_updater, _clock))
        {
            first.Start();
            _clock.Advance(FirstCheck);
            first.Current.IsBannerVisible.Should().BeTrue();

            first.Later();
            first.Current.IsBannerVisible.Should().BeFalse();
            for (var i = 0; i < 3; i++)
            {
                _clock.Advance(Interval);
            }

            first.Current.IsBannerVisible.Should().BeFalse("later checks don't bring it back in this session");
            first.Current.State.Should().Be(UpdateState.Ready);
        }

        // Next start: the downloaded update is pending (e.g. Windows shut down before an exit applied it).
        _updater.PendingRestart = V011;
        _updater.Available = null;
        await using var next = new UpdateService(_updater, _clock);
        next.Start();

        next.Current.IsBannerVisible.Should().BeTrue("ready again right away, without a network check");
        _updater.Checks.Should().Be(4);
    }

    [Fact]
    public async Task Never_restarts_on_its_own_exit_installs_without_restart()
    {
        _updater.Available = V011;
        await using var sut = new UpdateService(_updater, _clock);
        var restartRequests = 0;
        sut.RestartRequested += (_, _) => restartRequests++;
        sut.Start();

        _clock.Advance(FirstCheck);
        _clock.Advance(TimeSpan.FromDays(7)); // a week of 12 h checks with an update ready

        restartRequests.Should().Be(0);
        _updater.Applied.Should().BeEmpty();

        sut.PrepareForExit();
        _updater.Applied.Should().ContainSingle().Which.Should().Be((V011, false));
        restartRequests.Should().Be(0);
    }

    [Fact]
    public async Task Restart_now_applies_with_restart_and_asks_the_app_to_exit_once()
    {
        _updater.Available = V011;
        await using var sut = new UpdateService(_updater, _clock);
        var restartRequests = 0;
        sut.RestartRequested += (_, _) => restartRequests++;
        sut.Start();
        _clock.Advance(FirstCheck);

        sut.RestartNow().Should().BeTrue();
        sut.PrepareForExit(); // the exit that follows must not schedule a second apply

        restartRequests.Should().Be(1);
        _updater.Applied.Should().ContainSingle().Which.Should().Be((V011, true));
    }

    [Fact]
    public async Task Restart_now_without_a_ready_update_does_nothing()
    {
        await using var sut = new UpdateService(_updater, _clock);
        var restartRequests = 0;
        sut.RestartRequested += (_, _) => restartRequests++;
        sut.Start();

        sut.RestartNow().Should().BeFalse();
        sut.PrepareForExit();

        restartRequests.Should().Be(0);
        _updater.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Offline_is_quiet_and_retried_at_the_next_interval()
    {
        _updater.CheckFailure = new HttpRequestException("offline");
        await using var sut = new UpdateService(_updater, _clock);
        sut.Start();

        _clock.Advance(FirstCheck);
        sut.Current.State.Should().Be(UpdateState.Failed);
        sut.Current.IsBannerVisible.Should().BeFalse();
        sut.Current.LastResult.Should().Be(UpdateService.FailedText);

        _updater.CheckFailure = null;
        _clock.Advance(Interval);
        _updater.Checks.Should().Be(2);
        sut.Current.State.Should().Be(UpdateState.UpToDate);
    }

    [Fact]
    public async Task Manual_check_finds_and_downloads_at_once()
    {
        _updater.Available = V011;
        await using var sut = new UpdateService(_updater, _clock);
        sut.Start();

        await sut.CheckNowAsync();

        sut.Current.State.Should().Be(UpdateState.Ready);
        _updater.Downloads.Should().Be(1);
        _clock.Advance(FirstCheck);
        _updater.Downloads.Should().Be(1, "the same version isn't downloaded twice");
    }

    [Fact]
    public void Describe_shows_the_last_check_time_and_result()
    {
        var snapshot = new UpdateSnapshot(UpdateState.UpToDate, null, new DateTimeOffset(2026, 11, 13, 13, 5, 0, TimeSpan.Zero), "Up to date", false);

        UpdateService.Describe(snapshot, TimeZoneInfo.Utc).Should().Be("Last checked 2026-11-13 13:05 · Up to date");
        UpdateService.Describe(snapshot with { State = UpdateState.Idle, LastCheckAt = null, LastResult = null }).Should().Be("Not checked yet");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}

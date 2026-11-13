using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.Core.Tests.Connection;

/// <summary>
/// Prompt 9 Part C budget: nothing polls faster than the 250 ms phone-level poll, and that only while Connected;
/// every other periodic timer is at least 10 s.
/// </summary>
public sealed class PerformanceBudgetTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public void Periodic_timers_are_at_least_10s_and_the_fast_polls_are_at_least_250ms()
    {
        var coordinator = new ConnectionCoordinatorOptions();
        var reconnect = new ReconnectOptions();

        coordinator.HealthCheckInterval.Should().BeGreaterThanOrEqualTo(TenSeconds);
        reconnect.SlowRetryInterval.Should().BeGreaterThanOrEqualTo(TenSeconds);
        coordinator.StaleSessionPollInterval.Should().BeGreaterThanOrEqualTo(PhoneAudioOptions.MinPollInterval);
        new PhoneAudioOptions { PollInterval = TimeSpan.FromMilliseconds(10) }.PollInterval.Should().Be(TimeSpan.FromMilliseconds(10),
            "the record keeps what was asked; the service clamps it (PhoneAudioServiceTests)");
    }

    [Fact]
    public async Task Idle_coordinator_schedules_nothing_and_never_reads_the_audio_stack()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();

        for (var i = 0; i < 60; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        h.Clock.RequestedDelays.Should().BeEmpty();
        h.StatusReads.Should().Be(0);
    }

    [Fact]
    public async Task Connected_without_a_stale_signature_only_runs_the_10s_link_check()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        await h.AdvanceAsync(TenSeconds); // the one-shot 5 s "Opened" confirmation from the connect itself
        var readsAfterConnect = h.StatusReads;
        var delaysAfterConnect = h.Clock.RequestedDelays.Count;

        for (var i = 0; i < 360; i++)
        {
            await h.AdvanceAsync(TenSeconds); // one hour
        }

        h.StatusReads.Should().Be(readsAfterConnect, "the coordinator doesn't poll sessions (PhoneAudioService owns the 250 ms poll)");
        h.Clock.RequestedDelays.Skip(delaysAfterConnect).Should().OnlyContain(d => d >= TenSeconds);
    }

    [Fact]
    public async Task Fix_audio_waits_for_session_events_instead_of_polling()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.SetPhoneSession(active: true);

        await h.Sut.FixAudioAsync();
        h.SetReceiverStateSilently(ReceiverState.Closed);
        var readsBefore = h.StatusReads;
        for (var i = 0; i < 24; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromMilliseconds(250)); // 6 s
        }

        await h.SettleAsync();
        (h.StatusReads - readsBefore).Should().BeLessThanOrEqualTo(3, "one read at 3 s, one at the 6 s deadline, one before the reopen");
    }
}

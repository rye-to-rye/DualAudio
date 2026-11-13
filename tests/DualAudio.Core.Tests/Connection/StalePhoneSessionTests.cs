using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Tests.Fakes;
using static DualAudio.Core.Connection.ConnectionState;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Connection;

/// <summary>BUG-001: the phone session is already Active (stale) before we connect; the first open plays nothing.</summary>
public sealed class StalePhoneSessionTests
{
    private static readonly TimeSpan Step = new ConnectionCoordinatorOptions().StaleSessionPollInterval;
    private static readonly TimeSpan Settle = new ConnectionCoordinatorOptions().LinkLossSettleWindow;
    private static readonly TimeSpan PrimingDelay = new ConnectionCoordinatorOptions().PrimingDelay;

    // Hardware: reopening 1 s after the priming close failed with UnknownFailure 0x8007001F, which the
    // platform now maps to PhoneUnreachable.
    private static readonly OpenResult PhoneNotReady = OpenResult.Failed(ErrorKind.PhoneUnreachable, "0x8007001F");

    [Fact]
    public void Priming_delay_defaults_to_3s_with_2_4_6s_retries()
    {
        var options = new ConnectionCoordinatorOptions();

        options.PrimingDelay.Should().Be(TimeSpan.FromSeconds(3));
        options.PrimingRetryDelays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Priming_reopen_that_fails_once_is_retried_after_2s_and_connects_without_any_Error()
    {
        await using var h = new CoordinatorHarness();
        await PrimeUntilReopenPendingAsync(h);
        h.SetOpenResult(PhoneNotReady);

        await h.AdvanceAsync(PrimingDelay);
        h.OpenCalls.Should().Be(2);
        h.Sut.CurrentState.Should().Be(Opening, "a retryable priming failure stays in Opening");

        h.SetOpenResult(OpenResult.Ok());
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(1999));
        h.OpenCalls.Should().Be(2);
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        h.OpenCalls.Should().Be(3);

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
        h.Sut.LastError.Should().BeNull();
        h.States.Should().Equal(Enabling, Opening, Connected);
    }

    [Fact]
    public async Task All_priming_reopens_failing_goes_Disconnected_and_schedules_a_reconnect()
    {
        await using var h = new CoordinatorHarness();
        await PrimeUntilReopenPendingAsync(h);
        h.SetOpenResult(PhoneNotReady);

        await h.AdvanceAsync(PrimingDelay);           // reopen 1/4
        await h.AdvanceAsync(TimeSpan.FromSeconds(2)); // reopen 2/4
        await h.AdvanceAsync(TimeSpan.FromSeconds(4)); // reopen 3/4
        h.Sut.CurrentState.Should().Be(Opening);
        await h.AdvanceAsync(TimeSpan.FromSeconds(6)); // reopen 4/4

        h.OpenCalls.Should().Be(5, "the priming open plus 4 reopens");
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.LastError.Should().Be(ErrorKind.PhoneUnreachable);
        h.Sut.IsReconnecting.Should().BeTrue("0x8007001F can mean 'phone not ready', so it is not terminal");
        h.States.Should().Equal(Enabling, Opening, Disconnected);

        // The normal reconnect policy takes over (no second priming cycle).
        await h.AdvanceAsync(new ReconnectOptions().InitialDelay);
        h.OpenCalls.Should().Be(6);
    }

    [Fact]
    public async Task Stale_session_runs_one_priming_cycle_and_UI_stays_Opening_until_the_second_open()
    {
        await using var h = new CoordinatorHarness();
        h.SetPhoneSession(active: true);
        await h.AddDeviceAsync();

        await h.Sut.ConnectAsync(PhoneId);
        await h.SettleAsync();
        h.OpenCalls.Should().Be(1);

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Opening, "the first open only primes the pipeline");
        h.CloseCalls.Should().Be(1);

        // The priming close's own Closed event must not look like a link loss.
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(2999));
        h.OpenCalls.Should().Be(1, "the reopen waits the 3 s priming delay");

        await h.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        h.OpenCalls.Should().Be(2);

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
        h.States.Should().Equal(Enabling, Opening, Connected);
    }

    [Fact]
    public async Task Inactive_session_before_connect_means_no_extra_cycle()
    {
        await using var h = new CoordinatorHarness();
        h.SetPhoneSession(active: false);
        await h.AddDeviceAsync();

        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        await StepAsync(h, TimeSpan.FromSeconds(10));

        h.Sut.CurrentState.Should().Be(Connected);
        h.States.Should().Equal(Enabling, Opening, Connected);
        h.OpenCalls.Should().Be(1);
        h.CloseCalls.Should().Be(0);
        h.StatusReads.Should().Be(1, "only the pre-open check; no safety net without a stale session");
    }

    [Fact]
    public async Task Priming_never_repeats_within_one_user_connect_even_across_reconnects()
    {
        await using var h = new CoordinatorHarness();
        await PrimeToConnectedAsync(h);
        h.SetPhoneSession(active: true, peak: 0.1f); // still Active, but with audio: the safety net stays quiet

        // Link drops; the auto-reconnect finds the session Active again but must not prime a second time.
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);
        await h.AdvanceAsync(new ReconnectOptions().InitialDelay);
        h.OpenCalls.Should().Be(3);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected, "the reconnect's first Opened counts");
        h.States.Should().Equal(Disconnected, Enabling, Opening, Connected);
        await StepAsync(h, TimeSpan.FromSeconds(20));
        h.OpenCalls.Should().Be(3);
    }

    [Fact]
    public async Task Safety_net_reopens_exactly_once_when_the_stale_session_shows_no_activity()
    {
        await using var h = new CoordinatorHarness();
        await PrimeToConnectedAsync(h); // session stays Active with peak 0: the BUG-001 symptom

        await StepAsync(h, TimeSpan.FromSeconds(8) - Step);
        h.Sut.CurrentState.Should().Be(Connected, "8 s have not passed yet");

        await StepAsync(h, Step);
        h.Sut.CurrentState.Should().Be(Opening);
        h.CloseCalls.Should().Be(2);

        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(PrimingDelay);
        h.OpenCalls.Should().Be(3);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.States.Should().Equal(Opening, Connected); // never Disconnected

        // Still no activity: never a second safety-net cycle.
        await StepAsync(h, TimeSpan.FromSeconds(20));
        h.Sut.CurrentState.Should().Be(Connected);
        h.OpenCalls.Should().Be(3);
    }

    [Fact]
    public async Task Safety_net_stays_quiet_when_the_session_goes_Inactive_then_Active()
    {
        await using var h = new CoordinatorHarness();
        await PrimeToConnectedAsync(h);

        h.SetPhoneSession(active: false);
        await StepAsync(h, TimeSpan.FromSeconds(1));
        h.SetPhoneSession(active: true);
        await StepAsync(h, TimeSpan.FromSeconds(10));

        h.Sut.CurrentState.Should().Be(Connected);
        h.OpenCalls.Should().Be(2);
        h.Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnect_during_the_priming_delay_cancels_the_reopen()
    {
        await using var h = new CoordinatorHarness();
        h.SetPhoneSession(active: true);
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        await h.Sut.DisconnectAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));

        h.OpenCalls.Should().Be(1);
        h.States.Should().Equal(Enabling, Opening, Disconnected);
    }

    [Fact]
    public async Task Dispose_closes_the_connection_before_checking_the_phone_session()
    {
        var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.Sessions.ClearReceivedCalls();

        await h.DisposeAsync();

        Received.InOrder(() =>
        {
            h.Receiver.CloseAsync();
            h.Sessions.GetPhoneAudioStatusAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>Stale session (Active, peak 0) -> priming cycle -> Connected; clears transitions.</summary>
    private static async Task PrimeToConnectedAsync(CoordinatorHarness h)
    {
        h.SetPhoneSession(active: true);
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(new ConnectionCoordinatorOptions().PrimingDelay);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
        h.OpenCalls.Should().Be(2);
        h.ClearTransitions();
    }

    /// <summary>Stale session -> priming open reaches Opened and is closed; the first reopen is now pending.</summary>
    private static async Task PrimeUntilReopenPendingAsync(CoordinatorHarness h)
    {
        h.SetPhoneSession(active: true);
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.SettleAsync();
        h.OpenCalls.Should().Be(1);
        h.CloseCalls.Should().Be(1);
    }

    /// <summary>Advances in poll-sized steps so every safety-net poll runs, like real time.</summary>
    private static async Task StepAsync(CoordinatorHarness h, TimeSpan by)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += Step)
        {
            await h.AdvanceAsync(Step);
        }
    }
}

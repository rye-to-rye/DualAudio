using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Tests.Fakes;
using static DualAudio.Core.Connection.ConnectionState;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Connection;

/// <summary>Prompt 9 Part B: sleep/resume, lock/unlock, adapter removed/returned, silent Closed, slow retries, logoff.</summary>
public sealed class SystemEventsRecoveryTests
{
    private static readonly TimeSpan Settle = new ConnectionCoordinatorOptions().LinkLossSettleWindow;
    private static readonly TimeSpan FirstRetry = new ReconnectOptions().InitialDelay;
    private static readonly TimeSpan PrimingDelay = new ConnectionCoordinatorOptions().PrimingDelay;
    private static readonly TimeSpan SlowInterval = new ReconnectOptions().SlowRetryInterval;
    private static readonly OpenResult PhoneUnreachable = OpenResult.Failed(ErrorKind.PhoneUnreachable, "0x8007001F");

    // ---------------------------------------------------------------- sleep / resume

    [Fact]
    public async Task Resume_while_connected_releases_the_old_link_and_reconnects_through_the_policy()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseSuspending();
        h.RaiseResumed();
        await h.SettleAsync();

        h.CloseCalls.Should().Be(1, "the stale connection object is disposed");
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.IsReconnecting.Should().BeTrue();

        h.SetReceiverStateSilently(ReceiverState.Closed);
        await h.AdvanceAsync(FirstRetry);
        h.OpenCalls.Should().Be(2);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
        h.States.Should().Equal(Disconnected, Enabling, Opening, Connected);
    }

    [Fact]
    public async Task Resume_runs_the_priming_check_again_even_after_a_primed_connect()
    {
        await using var h = new CoordinatorHarness();
        h.SetPhoneSession(active: true); // stale at start-up: primes
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(PrimingDelay);
        h.RaiseReceiver(ReceiverState.Opened);
        h.SetPhoneSession(active: true, peak: 0.1f); // audio flows; the safety net stays quiet
        await h.SettleAsync();
        h.OpenCalls.Should().Be(2);

        h.RaiseResumed();
        await h.SettleAsync();
        h.SetPhoneSession(active: true); // stale again after the sleep
        h.SetReceiverStateSilently(ReceiverState.Closed);
        await h.AdvanceAsync(FirstRetry);
        h.OpenCalls.Should().Be(3);

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Opening, "after resume the stale pipeline is primed again");
        h.Sut.IsPriming.Should().BeTrue();

        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(PrimingDelay);
        h.OpenCalls.Should().Be(4);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Resume_does_nothing_when_idle_or_after_a_user_Disconnect()
    {
        await using var h = new CoordinatorHarness();
        h.RaiseResumed();
        await h.SettleAsync();
        h.States.Should().BeEmpty();

        await h.ConnectFullyAsync();
        await h.Sut.DisconnectAsync();
        await h.SettleAsync();
        h.ClearTransitions();

        h.RaiseResumed();
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));

        h.States.Should().BeEmpty();
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Resume_after_giving_up_tries_again()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1, SlowRetryInterval = TimeSpan.Zero });
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneUnreachable);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(FirstRetry);
        h.Sut.GaveUp.Should().BeTrue();

        h.SetOpenResult(OpenResult.Ok());
        h.RaiseResumed();
        await h.AdvanceAsync(FirstRetry);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
        h.OpenCalls.Should().Be(3);
    }

    [Fact]
    public async Task Resume_with_Bluetooth_not_back_yet_waits_for_the_radio()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.Radio.Status.Returns(RadioStatus.Off);

        h.RaiseResumed();
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);

        h.SetReceiverStateSilently(ReceiverState.Closed);
        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
    }

    // ---------------------------------------------------------------- lock / unlock

    [Fact]
    public async Task Lock_and_unlock_leave_a_healthy_link_alone()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseLocked();
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        h.RaiseUnlocked();
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));

        h.States.Should().BeEmpty();
        h.CloseCalls.Should().Be(0);
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Unlock_notices_a_link_that_dropped_silently_and_recovers()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.SetReceiverStateSilently(ReceiverState.Closed);

        h.RaiseUnlocked();
        await h.AdvanceAsync(Settle);

        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.IsReconnecting.Should().BeTrue();
    }

    // ---------------------------------------------------------------- silent state loss

    [Fact]
    public async Task Receiver_Closed_without_an_event_is_detected_within_10s_and_treated_as_a_lost_link()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.SetReceiverStateSilently(ReceiverState.Closed);

        await h.AdvanceAsync(TimeSpan.FromSeconds(9.9));
        h.Sut.CurrentState.Should().Be(Connected);

        await h.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        await h.AdvanceAsync(Settle);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.IsReconnecting.Should().BeTrue();

        await h.AdvanceAsync(FirstRetry);
        h.OpenCalls.Should().Be(2);
    }

    [Fact]
    public async Task Periodic_check_leaves_a_healthy_link_alone()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        for (var i = 0; i < 12; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(10));
        }

        h.States.Should().BeEmpty();
        h.OpenCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- adapter removed / returned

    [Fact]
    public async Task Adapter_removed_goes_NoAdapter_and_reconnects_by_itself_when_it_returns()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        // USB dongle unplugged / driver restart: Closed + radio gone + phone removed in one burst.
        h.RaiseReceiver(ReceiverState.Closed);
        h.RaiseRadio(RadioStatus.NotPresent);
        h.RemoveDevice();
        await h.AdvanceAsync(Settle);
        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.NoAdapter);

        // Adapter back, but its radio is off for a moment: show the true reason.
        h.RaiseRadio(RadioStatus.Off);
        await h.SettleAsync();
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);

        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(WaitingForPhone);
        await h.AddDeviceAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
        h.States.Should().Equal(Error, Error, Enabling, WaitingForPhone, Opening, Connected);
        h.Transitions[0].Error.Should().Be(ErrorKind.NoAdapter);
        h.Transitions[1].Error.Should().Be(ErrorKind.BluetoothOff);
    }

    [Fact]
    public async Task Adapter_removed_while_reconnecting_recovers_when_it_returns()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);
        h.Sut.IsReconnecting.Should().BeTrue();

        h.RaiseRadio(RadioStatus.NotPresent);
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        h.Sut.LastError.Should().Be(ErrorKind.NoAdapter);
        h.OpenCalls.Should().Be(1, "no attempts while there is no adapter");

        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
    }

    // ---------------------------------------------------------------- slow background retry

    [Fact]
    public async Task Slow_retry_keeps_trying_every_60s_after_the_fast_retries_and_connects()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1 });
        await h.AddDeviceAsync();
        h.SetOpenFailure(ErrorKind.Unknown);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(FirstRetry);
        h.Sut.GaveUp.Should().BeTrue();
        h.OpenCalls.Should().Be(2);

        await h.AdvanceAsync(SlowInterval - TimeSpan.FromSeconds(1));
        h.OpenCalls.Should().Be(2);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        h.OpenCalls.Should().Be(3);
        h.Sut.GaveUp.Should().BeTrue("a failed slow attempt returns to the calm Phone not found");

        await h.AdvanceAsync(SlowInterval);
        h.OpenCalls.Should().Be(4, "other failures don't pause slow retries");
        h.Sut.IsSlowRetryPaused.Should().BeFalse();

        h.SetOpenResult(OpenResult.Ok());
        await h.AdvanceAsync(SlowInterval);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
        h.Sut.GaveUp.Should().BeFalse();

        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        h.OpenCalls.Should().Be(5, "no slow retries once connected");
    }

    [Fact]
    public async Task Slow_retry_pauses_after_two_0x8007001F_until_the_watcher_reports_the_phone_again()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1 });
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneUnreachable);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(FirstRetry);
        await h.AdvanceAsync(SlowInterval);
        await h.AdvanceAsync(SlowInterval);
        h.OpenCalls.Should().Be(4);
        h.Sut.IsSlowRetryPaused.Should().BeTrue();

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.OpenCalls.Should().Be(4);

        // Phone Bluetooth back on: the watcher reports an Updated for it.
        h.RaiseDeviceUpdated();
        await h.SettleAsync();
        h.Sut.IsSlowRetryPaused.Should().BeFalse();
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        h.OpenCalls.Should().Be(5);

        // Still unreachable: paused again at once; an Updated caused by our own attempt is ignored.
        h.Sut.IsSlowRetryPaused.Should().BeTrue();
        h.RaiseDeviceUpdated();
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        h.OpenCalls.Should().Be(5);

        h.SetOpenResult(OpenResult.Ok());
        h.RaiseDeviceUpdated();
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        h.OpenCalls.Should().Be(6);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Retry_while_slow_retries_are_paused_starts_a_fresh_attempt()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1 });
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneUnreachable);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(FirstRetry);
        await h.AdvanceAsync(SlowInterval);
        await h.AdvanceAsync(SlowInterval);
        h.Sut.IsSlowRetryPaused.Should().BeTrue();

        h.SetOpenResult(OpenResult.Ok());
        await h.Sut.AutoConnectAsync(PhoneId); // "Phone not found — Retry"
        await h.SettleAsync();

        h.OpenCalls.Should().Be(5);
        h.Sut.IsSlowRetryPaused.Should().BeFalse();
    }

    [Fact]
    public async Task User_Disconnect_stops_slow_retries()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1 });
        await h.AddDeviceAsync();
        h.SetOpenFailure(ErrorKind.Unknown);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(FirstRetry);
        h.Sut.GaveUp.Should().BeTrue();

        await h.Sut.DisconnectAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.OpenCalls.Should().Be(2);
        h.Sut.CurrentState.Should().Be(Disconnected);
    }

    [Fact]
    public async Task No_slow_retries_for_a_phone_the_watcher_does_not_report()
    {
        await using var h = new CoordinatorHarness();
        await h.Sut.AutoConnectAsync(PhoneId); // enumeration complete, phone not listed
        await h.SettleAsync();
        h.Sut.GaveUp.Should().BeTrue();

        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.OpenCalls.Should().Be(0, "a watcher Added starts the next attempt instead");
    }

    // ---------------------------------------------------------------- logoff / shutdown

    [Fact]
    public async Task Session_ending_closes_the_connection_before_returning()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseSessionEnding(); // the handler blocks until the close is done

        h.CloseCalls.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.IsReconnecting.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_logoff_reconnects_after_30s()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseSessionEnding();
        h.SetReceiverStateSilently(ReceiverState.Closed);

        await h.AdvanceAsync(TimeSpan.FromSeconds(29));
        h.OpenCalls.Should().Be(1);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        await h.AdvanceAsync(FirstRetry);
        h.OpenCalls.Should().Be(2);
    }
}

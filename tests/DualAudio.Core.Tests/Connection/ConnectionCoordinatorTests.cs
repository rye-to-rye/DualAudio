using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Tests.Fakes;
using static DualAudio.Core.Connection.ConnectionState;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Connection;

public sealed class ConnectionCoordinatorTests
{
    private static readonly TimeSpan Settle = new ConnectionCoordinatorOptions().LinkLossSettleWindow;

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task Starts_idle_with_no_device_or_error()
    {
        await using var h = new CoordinatorHarness();

        h.Sut.CurrentState.Should().Be(Idle);
        h.Sut.ActiveDevice.Should().BeNull();
        h.Sut.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Connect_goes_Enabling_Opening_then_Connected_on_Opened_event()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();

        await h.Sut.ConnectAsync(PhoneId);
        h.States.Should().Equal(Enabling, Opening);

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.States.Should().Equal(Enabling, Opening, Connected);
        h.Sut.ActiveDevice.Should().Be(new PhoneDevice(PhoneId, PhoneName, IsConnected: true));
        h.Sut.LastError.Should().BeNull();
        await h.Receiver.Received(1).OpenAsync(PhoneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Successful_OpenAsync_alone_does_not_mean_connected()
    {
        // Phase 0: State reads Closed right after a successful OpenAsync; Opened arrives ~1 ms later.
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();

        await h.Sut.ConnectAsync(PhoneId);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Opening);
    }

    [Fact]
    public async Task Opened_event_arriving_before_OpenAsync_returns_still_connects()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            h.RaiseReceiver(ReceiverState.Opened);
            return Task.FromResult(OpenResult.Ok());
        });

        await h.Sut.ConnectAsync(PhoneId);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Missing_Opened_event_falls_back_to_receiver_state_after_confirmation_timeout()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);
        h.Receiver.State.Returns(ReceiverState.Opened);

        await h.AdvanceAsync(h.Options.OpenedConfirmationTimeout);

        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Missing_Opened_event_with_closed_receiver_is_OpenTimedOut()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);

        await h.AdvanceAsync(h.Options.OpenedConfirmationTimeout);

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.OpenTimedOut);
        h.CloseCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Connect_before_enumeration_waits_for_phone_then_opens_when_added()
    {
        await using var h = new CoordinatorHarness();
        h.Watcher.IsEnumerationCompleted.Returns(false);

        await h.Sut.ConnectAsync(PhoneId);
        h.Sut.CurrentState.Should().Be(WaitingForPhone);
        h.OpenCalls.Should().Be(0);

        await h.AddDeviceAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.States.Should().Equal(Enabling, WaitingForPhone, Opening, Connected);
    }

    [Fact]
    public async Task Waiting_for_phone_that_is_not_in_the_completed_enumeration_is_DeviceNotPaired()
    {
        await using var h = new CoordinatorHarness();
        h.Watcher.IsEnumerationCompleted.Returns(false);
        await h.Sut.ConnectAsync(PhoneId);

        h.CompleteEnumeration();
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotPaired);
    }

    [Fact]
    public async Task Connect_to_unknown_device_after_enumeration_is_DeviceNotPaired()
    {
        await using var h = new CoordinatorHarness();

        await h.Sut.ConnectAsync(PhoneId);

        h.States.Should().Equal(Enabling, Error);
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotPaired);
        h.OpenCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(RadioStatus.Off, ErrorKind.BluetoothOff)]
    [InlineData(RadioStatus.Disabled, ErrorKind.BluetoothOff)]
    [InlineData(RadioStatus.NotPresent, ErrorKind.NoAdapter)]
    public async Task Connect_with_radio_unavailable_fails_without_opening(RadioStatus radio, ErrorKind expected)
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.Radio.Status.Returns(radio);

        await h.Sut.ConnectAsync(PhoneId);

        h.States.Should().Equal(Enabling, Error);
        h.Sut.LastError.Should().Be(expected);
        h.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Connect_requested_while_Bluetooth_off_resumes_when_radio_turns_on()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.Radio.Status.Returns(RadioStatus.Off);
        await h.Sut.ConnectAsync(PhoneId);
        h.ClearTransitions();

        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.States.Should().Equal(Enabling, Opening, Connected);
    }

    [Theory]
    [InlineData(ErrorKind.SinkNotSupported)]
    [InlineData(ErrorKind.DeniedBySystem)]
    [InlineData(ErrorKind.DeviceNotFound)]
    [InlineData(ErrorKind.Unknown)]
    public async Task Failed_open_maps_to_Error_with_that_kind(ErrorKind kind)
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.SetOpenFailure(kind);

        await h.Sut.ConnectAsync(PhoneId);

        h.States.Should().Equal(Enabling, Opening, Error);
        h.Sut.LastError.Should().Be(kind);
        h.Transitions[^1].Error.Should().Be(kind);
        h.CloseCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OpenAsync_that_never_finishes_times_out_and_is_cancelled()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        CancellationToken seen = default;
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            seen = ci.ArgAt<CancellationToken>(1);
            return new TaskCompletionSource<OpenResult>().Task;
        });

        var connect = h.Sut.ConnectAsync(PhoneId);
        h.Sut.CurrentState.Should().Be(Opening);
        h.Clock.Advance(h.Options.OpenTimeout);
        await connect.WaitAsync(TimeSpan.FromSeconds(10));

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.OpenTimedOut);
        seen.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task OpenAsync_throwing_becomes_Error_Unknown_and_does_not_throw_to_caller()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs<Task<OpenResult>>(_ => throw new InvalidOperationException("boom"));

        var act = () => h.Sut.ConnectAsync(PhoneId);

        await act.Should().NotThrowAsync();
        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.Unknown);
    }

    [Fact]
    public async Task Connect_to_another_device_while_connected_closes_first_then_opens_new()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        await h.AddDeviceAsync(OtherPhoneId, "Tablet");

        await h.Sut.ConnectAsync(OtherPhoneId);
        h.RaiseReceiver(ReceiverState.Opened, OtherPhoneId);
        await h.SettleAsync();

        h.CloseCalls.Should().BeGreaterThan(0);
        await h.Receiver.Received(1).OpenAsync(OtherPhoneId, Arg.Any<CancellationToken>());
        h.Sut.ActiveDevice!.Name.Should().Be("Tablet");
        h.Sut.CurrentState.Should().Be(Connected);
    }

    // ------------------------------------------------------------------ user disconnect

    [Fact]
    public async Task Disconnect_from_Connected_releases_link_and_ignores_the_resulting_Closed()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        await h.Sut.DisconnectAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.States.Should().Equal(Disconnected);
        h.Sut.ActiveDevice.Should().BeNull();
        h.Sut.IsReconnecting.Should().BeFalse();
        h.CloseCalls.Should().Be(1);
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Disconnect_during_a_slow_open_cancels_it_without_an_error()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        CancellationToken seen = default;
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            seen = ci.ArgAt<CancellationToken>(1);
            var tcs = new TaskCompletionSource<OpenResult>();
            seen.Register(() => tcs.TrySetCanceled(seen));
            return tcs.Task;
        });

        var connect = h.Sut.ConnectAsync(PhoneId);
        await h.Sut.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await connect.WaitAsync(TimeSpan.FromSeconds(10));

        seen.IsCancellationRequested.Should().BeTrue();
        h.States.Should().Equal(Enabling, Opening, Disconnected);
        h.Sut.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_clears_an_error()
    {
        await using var h = new CoordinatorHarness();
        await h.Sut.ConnectAsync(PhoneId); // -> Error(DeviceNotPaired)

        await h.Sut.DisconnectAsync();

        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.LastError.Should().BeNull();
    }

    // ------------------------------------------------------------------ illegal / irrelevant events

    [Fact]
    public async Task Receiver_events_while_idle_are_ignored()
    {
        await using var h = new CoordinatorHarness();

        h.RaiseReceiver(ReceiverState.Opened);
        h.RaiseReceiver(ReceiverState.Closed);
        await h.SettleAsync();

        h.Transitions.Should().BeEmpty();
        h.Sut.CurrentState.Should().Be(Idle);
    }

    [Fact]
    public async Task Disconnect_while_idle_is_a_no_op()
    {
        await using var h = new CoordinatorHarness();

        await h.Sut.DisconnectAsync();

        h.Transitions.Should().BeEmpty();
        h.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Radio_changes_while_idle_are_ignored()
    {
        await using var h = new CoordinatorHarness();

        h.RaiseRadio(RadioStatus.Off);
        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();

        h.Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Connect_to_the_device_already_connected_is_ignored()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        await h.Sut.ConnectAsync(PhoneId);

        h.Transitions.Should().BeEmpty();
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Opened_for_another_device_does_not_complete_the_current_open()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        await h.Sut.ConnectAsync(PhoneId);

        h.RaiseReceiver(ReceiverState.Opened, OtherPhoneId);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Opening);
    }

    [Fact]
    public async Task Second_Opened_while_connected_is_ignored()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Removal_of_another_device_while_connected_is_ignored()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync(OtherPhoneId, "Tablet");
        await h.ConnectFullyAsync();

        h.RemoveDevice(OtherPhoneId);
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));

        h.Transitions.Should().BeEmpty();
        h.Sut.Devices.Select(d => d.Id).Should().Equal(PhoneId);
    }

    [Fact]
    public async Task A_throwing_StateChanged_subscriber_does_not_break_the_state_machine()
    {
        await using var h = new CoordinatorHarness();
        h.Sut.StateChanged += (_, _) => throw new InvalidOperationException("bad UI handler");
        await h.AddDeviceAsync();

        await h.Sut.ConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task StateChanged_handler_can_call_DisconnectAsync_synchronously_without_deadlock()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        bool? disconnectCompletedInHandler = null;
        h.Sut.StateChanged += (_, e) =>
        {
            if (e.Current == Connected)
            {
                // Blocking on purpose: if the event were raised while the coordinator holds its
                // (non-re-entrant) transition lock, this wait could never succeed.
#pragma warning disable xUnit1031
                disconnectCompletedInHandler = h.Sut.DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
            }
        };

        await h.Sut.ConnectAsync(PhoneId).WaitAsync(TimeSpan.FromSeconds(10));
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        disconnectCompletedInHandler.Should().BeTrue();
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.ActiveDevice.Should().BeNull();
        h.States.Should().Equal(Enabling, Opening, Connected, Disconnected);
        h.Transitions[^1].Previous.Should().Be(Connected);
    }

    [Fact]
    public async Task Enabling_and_Opening_are_raised_before_a_slow_OpenAsync_completes()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        var open = new TaskCompletionSource<OpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(open.Task);
        var openingSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sut.StateChanged += (_, e) =>
        {
            if (e.Current == Opening)
            {
                openingSeen.TrySetResult();
            }
        };

        var connect = h.Sut.ConnectAsync(PhoneId);
        await openingSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        open.Task.IsCompleted.Should().BeFalse();
        connect.IsCompleted.Should().BeFalse("the transition is still awaiting OpenAsync");
        h.States.Should().Equal(Enabling, Opening);

        open.SetResult(OpenResult.Ok());
        await connect.WaitAsync(TimeSpan.FromSeconds(5));
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.States.Should().Equal(Enabling, Opening, Connected);
    }

    [Fact]
    public async Task Handler_blocking_on_DisconnectAsync_during_a_slow_OpenAsync_does_not_deadlock()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        var open = new TaskCompletionSource<OpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(open.Task);
        bool? disconnectCompletedInHandler = null;
        h.Sut.StateChanged += (_, e) =>
        {
            if (e.Current == Opening)
            {
                // Raised while the open is still running; must not be on the thread holding the transition lock.
#pragma warning disable xUnit1031
                disconnectCompletedInHandler = h.Sut.DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
            }
        };

        await h.Sut.ConnectAsync(PhoneId).WaitAsync(TimeSpan.FromSeconds(10));
        await h.SettleAsync();

        disconnectCompletedInHandler.Should().BeTrue();
        open.Task.IsCompleted.Should().BeFalse("the open was abandoned, not awaited to completion");
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.States.Should().Equal(Enabling, Opening, Disconnected);
    }

    // ------------------------------------------------------------------ device metadata

    [Fact]
    public async Task Name_from_Added_is_kept_when_Updated_reports_garbage()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        await h.UpdateDeviceAsync(PhoneId, "4"); // observed in Phase 0 after an idle drop

        h.Sut.Devices.Single().Name.Should().Be(PhoneName);
        h.Sut.ActiveDevice!.Name.Should().Be(PhoneName);
        h.Transitions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ link loss + reconnect

    [Fact]
    public async Task Unexpected_Closed_after_Opened_becomes_Disconnected_after_settle_and_schedules_reconnect()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseReceiver(ReceiverState.Closed);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected, "the settle window has not elapsed yet");

        await h.AdvanceAsync(Settle);

        h.States.Should().Equal(Disconnected);
        h.Sut.LastError.Should().BeNull();
        h.Sut.ActiveDevice!.Id.Should().Be(PhoneId);
        h.Sut.IsReconnecting.Should().BeTrue();
        h.CloseCalls.Should().Be(1);
    }

    [Fact]
    public async Task Idle_drop_reconnects_automatically_after_first_backoff_delay()
    {
        // BUG-002: link closed ~4 min after connect; a PC-initiated reconnect worked.
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1.9));
        h.OpenCalls.Should().Be(1);

        await h.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        h.OpenCalls.Should().Be(2);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.States.Should().Equal(Disconnected, Enabling, Opening, Connected);
        h.Sut.IsReconnecting.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_reconnect_attempts_back_off_exponentially_then_give_up_with_error()
    {
        // Fast policy only; the slow background retry after it is covered in SystemEventsRecoveryTests.
        var options = new ReconnectOptions { MaxAttempts = 3, SlowRetryInterval = TimeSpan.Zero };
        await using var h = new CoordinatorHarness(reconnect: options);
        await h.ConnectFullyAsync();
        h.SetOpenFailure(ErrorKind.DeviceNotFound);
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);

        await h.AdvanceAsync(TimeSpan.FromSeconds(2));
        h.OpenCalls.Should().Be(2);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotFound);

        await h.AdvanceAsync(TimeSpan.FromSeconds(3.9));
        h.OpenCalls.Should().Be(2, "the second retry waits 4 s");
        await h.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        h.OpenCalls.Should().Be(3);

        await h.AdvanceAsync(TimeSpan.FromSeconds(8));
        h.OpenCalls.Should().Be(4);
        h.Sut.CurrentState.Should().Be(Error, "MaxAttempts reached");
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotFound);
        h.Sut.IsReconnecting.Should().BeFalse();

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.OpenCalls.Should().Be(4);
    }

    [Fact]
    public async Task Device_removed_while_connected_becomes_Disconnected_and_schedules_reconnect()
    {
        await using var h = new CoordinatorHarness(autoReconnect: true);
        await h.ConnectFullyAsync();

        h.RemoveDevice();
        await h.AdvanceAsync(Settle);

        h.States.Should().Equal(Disconnected);
        h.Sut.IsReconnecting.Should().BeTrue();

        // The phone is gone from the watcher, so the attempt waits for it to come back.
        await h.AdvanceAsync(TimeSpan.FromSeconds(2));
        h.Sut.CurrentState.Should().Be(WaitingForPhone);
        await h.AddDeviceAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Device_removed_without_auto_reconnect_becomes_Disconnected_only()
    {
        await using var h = new CoordinatorHarness(autoReconnect: false);
        await h.ConnectFullyAsync();

        h.RemoveDevice();
        await h.AdvanceAsync(Settle);
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.States.Should().Equal(Disconnected);
        h.Sut.IsReconnecting.Should().BeFalse();
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task User_Disconnect_cancels_a_pending_reconnect()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);
        h.Sut.IsReconnecting.Should().BeTrue();

        await h.Sut.DisconnectAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.Sut.IsReconnecting.Should().BeFalse();
        h.Sut.ActiveDevice.Should().BeNull();
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task User_Disconnect_during_the_settle_window_wins_over_link_loss()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        await h.SettleAsync();

        await h.Sut.DisconnectAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.States.Should().Equal(Disconnected);
        h.Sut.IsReconnecting.Should().BeFalse();
        h.OpenCalls.Should().Be(1);
    }

    // ------------------------------------------------------------------ Bluetooth off

    [Fact]
    public async Task Radio_off_while_connected_is_Error_BluetoothOff()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseRadio(RadioStatus.Off);
        await h.SettleAsync();

        h.States.Should().Equal(Error);
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);
        h.CloseCalls.Should().Be(1);
    }

    [Fact]
    public async Task PC_Bluetooth_off_burst_Closed_Off_Removed_collapses_into_one_BluetoothOff_error()
    {
        // Phase 0: Closed -> radio Off -> watcher Removed within ~140 ms.
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseReceiver(ReceiverState.Closed);
        h.Clock.Advance(TimeSpan.FromMilliseconds(70));
        h.RaiseRadio(RadioStatus.Off);
        h.Clock.Advance(TimeSpan.FromMilliseconds(70));
        h.RemoveDevice();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        h.Transitions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Previous = Connected, Current = Error, Error = (ErrorKind?)ErrorKind.BluetoothOff });
        h.Sut.IsReconnecting.Should().BeFalse();
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Bluetooth_off_burst_in_other_order_also_collapses_into_one_error()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RemoveDevice();
        h.RaiseReceiver(ReceiverState.Closed);
        h.RaiseRadio(RadioStatus.Off);
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        h.States.Should().Equal(Error);
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);
    }

    [Fact]
    public async Task Closed_then_radio_off_after_settle_check_still_reports_BluetoothOff_not_link_loss()
    {
        // Radio reports Off but the event is late: the settle-window check reads the radio status.
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();

        h.RaiseReceiver(ReceiverState.Closed);
        h.Radio.Status.Returns(RadioStatus.Off);
        await h.AdvanceAsync(Settle);

        h.States.Should().Equal(Error);
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);
    }

    [Fact]
    public async Task After_Bluetooth_returns_the_phone_reconnects_when_it_reappears()
    {
        await using var h = new CoordinatorHarness();
        await h.ConnectFullyAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        h.RaiseRadio(RadioStatus.Off);
        h.RemoveDevice();
        await h.AdvanceAsync(Settle);
        h.ClearTransitions();

        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(WaitingForPhone);
        await h.AddDeviceAsync();
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.States.Should().Equal(Enabling, WaitingForPhone, Opening, Connected);
    }

    [Fact]
    public async Task After_Bluetooth_returns_without_auto_reconnect_the_error_clears_to_Idle()
    {
        await using var h = new CoordinatorHarness(autoReconnect: false);
        await h.ConnectFullyAsync();
        h.RaiseRadio(RadioStatus.Off);
        await h.SettleAsync();
        h.ClearTransitions();

        h.RaiseRadio(RadioStatus.On);
        await h.SettleAsync();

        h.States.Should().Equal(Idle);
        h.Sut.LastError.Should().BeNull();
        h.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Radio_off_during_open_aborts_it_with_BluetoothOff()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            var token = ci.ArgAt<CancellationToken>(1);
            var tcs = new TaskCompletionSource<OpenResult>();
            token.Register(() => tcs.TrySetCanceled(token));
            return tcs.Task;
        });

        var connect = h.Sut.ConnectAsync(PhoneId);
        h.RaiseRadio(RadioStatus.Off);
        await connect.WaitAsync(TimeSpan.FromSeconds(10));
        await h.SettleAsync();

        h.States.Should().Equal(Enabling, Opening, Error);
        h.Sut.LastError.Should().Be(ErrorKind.BluetoothOff);
    }

    // ------------------------------------------------------------------ serialization

    [Fact]
    public async Task Concurrent_ConnectAsync_calls_are_serialized_and_open_only_once()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        var open = new TaskCompletionSource<OpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(open.Task);

        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.Sut.ConnectAsync(PhoneId))).ToArray();
        await Task.Delay(50);
        h.OpenCalls.Should().Be(1, "the other calls are queued behind the transition lock");

        open.SetResult(OpenResult.Ok());
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.OpenCalls.Should().Be(1);
        h.States.Should().Equal(Enabling, Opening, Connected);
    }

    [Fact]
    public async Task OpenAsync_never_runs_concurrently_even_for_different_devices()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        await h.AddDeviceAsync(OtherPhoneId, "Tablet");
        var concurrent = 0;
        var maxConcurrent = 0;
        h.Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
            return OpenResult.Ok();
        });

        await Task.WhenAll(
            Task.Run(() => h.Sut.ConnectAsync(PhoneId)),
            Task.Run(() => h.Sut.ConnectAsync(OtherPhoneId)),
            Task.Run(() => h.Sut.ConnectAsync(PhoneId)),
            Task.Run(() => h.Sut.ConnectAsync(OtherPhoneId))).WaitAsync(TimeSpan.FromSeconds(10));
        await h.SettleAsync();

        maxConcurrent.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Opening);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

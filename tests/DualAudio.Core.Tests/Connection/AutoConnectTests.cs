using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Tests.Fakes;
using static DualAudio.Core.Connection.ConnectionState;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Connection;

/// <summary>Start-up auto-connect to the remembered phone ("open app -> already connected").</summary>
public sealed class AutoConnectTests
{
    private static readonly TimeSpan Settle = new ConnectionCoordinatorOptions().LinkLossSettleWindow;
    private static readonly OpenResult PhoneBluetoothOff = OpenResult.Failed(ErrorKind.PhoneUnreachable, "0x8007001F");

    [Fact]
    public async Task Auto_connect_waits_for_the_phone_and_connects_when_the_watcher_adds_it()
    {
        await using var h = new CoordinatorHarness();
        h.Watcher.IsEnumerationCompleted.Returns(false);

        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(WaitingForPhone);
        h.OpenCalls.Should().Be(0);

        await h.AddDeviceAsync();
        h.OpenCalls.Should().Be(1, "the phone appearing in the watcher starts the open");
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
        h.Sut.GaveUp.Should().BeFalse();
    }

    [Fact]
    public async Task Auto_connect_when_enumeration_finishes_without_the_phone_is_calm_and_connects_if_it_appears_later()
    {
        await using var h = new CoordinatorHarness();
        h.Watcher.IsEnumerationCompleted.Returns(false);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();

        h.Watcher.IsEnumerationCompleted.Returns(true);
        h.CompleteEnumeration();
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotFound, "not DeviceNotPaired: this is the calm state");
        h.Sut.GaveUp.Should().BeTrue();
        h.Sut.ActiveDevice!.Id.Should().Be(PhoneId, "the remembered phone stays the target");

        await h.AddDeviceAsync();

        h.OpenCalls.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Opening);
        h.Sut.GaveUp.Should().BeFalse();
    }

    [Fact]
    public async Task Auto_connect_after_enumeration_already_finished_without_the_phone_is_calm()
    {
        await using var h = new CoordinatorHarness(); // enumeration completed, no devices

        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.GaveUp.Should().BeTrue();
    }

    [Fact]
    public async Task Manual_connect_to_an_unknown_phone_is_still_an_error_not_the_calm_state()
    {
        await using var h = new CoordinatorHarness();

        await h.Sut.ConnectAsync(PhoneId);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.DeviceNotPaired);
        h.Sut.GaveUp.Should().BeFalse();
    }

    [Fact]
    public async Task Phone_Bluetooth_off_at_start_is_retried_with_backoff_and_connects_by_itself_when_it_comes_on()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneBluetoothOff);

        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();

        h.OpenCalls.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.LastError.Should().Be(ErrorKind.PhoneUnreachable, "shown as \"Can't reach your phone\"");
        h.Sut.IsReconnecting.Should().BeTrue();

        await h.AdvanceAsync(TimeSpan.FromSeconds(2));
        h.OpenCalls.Should().Be(2);
        h.Sut.CurrentState.Should().Be(Disconnected);

        h.SetOpenResult(OpenResult.Ok()); // the user turns the phone's Bluetooth on
        await h.AdvanceAsync(TimeSpan.FromSeconds(4));
        h.OpenCalls.Should().Be(3);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();

        h.Sut.CurrentState.Should().Be(Connected);
        h.Sut.LastError.Should().BeNull();
        h.Sut.IsReconnecting.Should().BeFalse();
    }

    [Fact]
    public async Task Auto_connect_gives_up_calmly_after_max_attempts()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 2 });
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneBluetoothOff);

        await h.Sut.AutoConnectAsync(PhoneId);
        await h.AdvanceAsync(TimeSpan.FromSeconds(2));
        await h.AdvanceAsync(TimeSpan.FromSeconds(4));

        h.OpenCalls.Should().Be(3, "the first try plus MaxAttempts retries");
        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.LastError.Should().Be(ErrorKind.PhoneUnreachable);
        h.Sut.GaveUp.Should().BeTrue();
        h.Sut.IsReconnecting.Should().BeFalse();

        // Prompt 9: two slow background attempts (60 s apart); both fail with 0x8007001F, so slow retries pause.
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        h.OpenCalls.Should().Be(5);
        h.Sut.IsSlowRetryPaused.Should().BeTrue();

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.OpenCalls.Should().Be(5, "paused until the watcher reports the phone again or the user presses Retry");
        h.Sut.GaveUp.Should().BeTrue();
    }

    [Fact]
    public async Task Exhausted_reconnect_after_a_link_loss_is_also_the_calm_state()
    {
        await using var h = new CoordinatorHarness(reconnect: new ReconnectOptions { MaxAttempts = 1 });
        await h.ConnectFullyAsync();
        h.SetOpenResult(PhoneBluetoothOff);

        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(Settle);
        await h.AdvanceAsync(TimeSpan.FromSeconds(2));

        h.Sut.CurrentState.Should().Be(Error);
        h.Sut.GaveUp.Should().BeTrue();
    }

    [Fact]
    public async Task User_disconnect_while_waiting_for_the_phone_means_no_connect_when_it_appears()
    {
        await using var h = new CoordinatorHarness();
        h.Watcher.IsEnumerationCompleted.Returns(false);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();

        await h.Sut.DisconnectAsync();
        await h.AddDeviceAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.OpenCalls.Should().Be(0);
        h.Sut.CurrentState.Should().Be(Disconnected);
    }

    [Fact]
    public async Task User_disconnect_during_auto_connect_retries_stops_them()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneBluetoothOff);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();
        h.Sut.IsReconnecting.Should().BeTrue();

        await h.Sut.DisconnectAsync();
        h.SetOpenResult(OpenResult.Ok());
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.OpenCalls.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.IsReconnecting.Should().BeFalse();
    }

    [Fact]
    public async Task No_reconnect_after_manual_disconnect_of_an_auto_connected_phone()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        await h.Sut.AutoConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);

        await h.Sut.DisconnectAsync();
        h.RaiseReceiver(ReceiverState.Closed);
        h.RemoveDevice();
        await h.AddDeviceAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        h.OpenCalls.Should().Be(1);
        h.Sut.CurrentState.Should().Be(Disconnected);
        h.Sut.ActiveDevice.Should().BeNull();
    }

    [Fact]
    public async Task User_disconnect_from_the_calm_state_means_no_connect_when_the_phone_appears()
    {
        await using var h = new CoordinatorHarness();
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();
        h.Sut.GaveUp.Should().BeTrue();

        await h.Sut.DisconnectAsync();
        await h.AddDeviceAsync();

        h.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Auto_connect_with_a_stale_phone_session_runs_the_BUG_001_priming_cycle()
    {
        await using var h = new CoordinatorHarness();
        h.SetPhoneSession(active: true); // left Active by the previous process
        await h.AddDeviceAsync();

        await h.Sut.AutoConnectAsync(PhoneId);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Opening, "the first open only primes the pipeline");
        h.RaiseReceiver(ReceiverState.Closed);
        await h.AdvanceAsync(new ConnectionCoordinatorOptions().PrimingDelay);

        h.OpenCalls.Should().Be(2);
        h.RaiseReceiver(ReceiverState.Opened);
        await h.SettleAsync();
        h.Sut.CurrentState.Should().Be(Connected);
    }

    [Fact]
    public async Task Auto_connect_does_not_interrupt_a_reconnect_already_running()
    {
        await using var h = new CoordinatorHarness();
        await h.AddDeviceAsync();
        h.SetOpenResult(PhoneBluetoothOff);
        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();
        var next = h.Sut.NextReconnectAt;

        await h.Sut.AutoConnectAsync(PhoneId);
        await h.SettleAsync();

        h.OpenCalls.Should().Be(1);
        h.Sut.NextReconnectAt.Should().Be(next);
    }
}

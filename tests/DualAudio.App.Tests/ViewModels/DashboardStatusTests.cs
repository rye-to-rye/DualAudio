using DualAudio.App.ViewModels;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;

namespace DualAudio.App.Tests.ViewModels;

public sealed class DashboardStatusTests
{
    [Theory]
    [InlineData(ConnectionState.Idle, false, false, false, StatusKind.Disconnected, "Disconnected")]
    [InlineData(ConnectionState.Disconnected, false, false, false, StatusKind.Disconnected, "Disconnected")]
    [InlineData(ConnectionState.Disconnected, true, false, false, StatusKind.Connecting, "Reconnecting…")]
    [InlineData(ConnectionState.Enabling, false, false, false, StatusKind.Connecting, "Connecting…")]
    [InlineData(ConnectionState.WaitingForPhone, false, false, false, StatusKind.Connecting, "Waiting for phone…")]
    [InlineData(ConnectionState.Opening, false, false, false, StatusKind.Connecting, "Connecting…")]
    [InlineData(ConnectionState.Connected, false, false, false, StatusKind.Connected, "Connected")]
    [InlineData(ConnectionState.Connected, false, true, false, StatusKind.Receiving, "Receiving audio")]
    [InlineData(ConnectionState.Connected, false, false, true, StatusKind.Connected, "Connected · Muted")]
    [InlineData(ConnectionState.Connected, false, true, true, StatusKind.Receiving, "Receiving audio · Muted")]
    [InlineData(ConnectionState.Error, false, false, false, StatusKind.Error, "Error")]
    public void Status_per_state(ConnectionState state, bool reconnecting, bool receiving, bool muted, StatusKind kind, string text)
    {
        DashboardStatus.From(state, reconnecting, receiving, muted).Should().Be(new DashboardStatus(kind, text));
    }

    [Theory]
    [InlineData(ErrorKind.BluetoothOff, ErrorAction.OpenBluetoothSettings, "Open Bluetooth settings")]
    [InlineData(ErrorKind.NoAdapter, ErrorAction.OpenBluetoothSettings, "Open Bluetooth settings")]
    [InlineData(ErrorKind.DeviceNotPaired, ErrorAction.OpenBluetoothSettings, "Open Bluetooth settings")]
    [InlineData(ErrorKind.SinkNotSupported, ErrorAction.None, null)]
    [InlineData(ErrorKind.DeviceNotFound, ErrorAction.Retry, "Retry")]
    [InlineData(ErrorKind.OpenTimedOut, ErrorAction.Retry, "Retry")]
    [InlineData(ErrorKind.DeniedBySystem, ErrorAction.Retry, "Retry")]
    [InlineData(ErrorKind.PhoneUnreachable, ErrorAction.Retry, "Retry")]
    [InlineData(ErrorKind.Unknown, ErrorAction.Retry, "Retry")]
    public void Error_banner_per_kind(ErrorKind kind, ErrorAction action, string? label)
    {
        var banner = ErrorBanner.From(ConnectionState.Error, kind, isReconnecting: false);

        banner.Should().NotBeNull();
        banner!.Title.Should().Be(ErrorMessages.For(kind).Title);
        banner.Message.Should().Be(ErrorMessages.For(kind).Action);
        banner.Action.Should().Be(action);
        banner.ActionLabel.Should().Be(label);
    }

    [Fact]
    public void Every_error_kind_has_a_banner_action_decision()
    {
        foreach (var kind in Enum.GetValues<ErrorKind>())
        {
            ErrorBanner.From(ConnectionState.Error, kind, false).Should().NotBeNull();
        }
    }

    [Fact]
    public void PhoneUnreachable_0x8007001F_reads_as_cant_reach_your_phone_not_turn_Bluetooth_off_and_on()
    {
        var banner = ErrorBanner.From(ConnectionState.Error, ErrorKind.PhoneUnreachable, false)!;

        banner.Title.Should().Be("Can't reach your phone");
        banner.Message.Should().Contain("Is Bluetooth on and is it nearby?")
            .And.NotContain("turn Bluetooth off and on");
    }

    [Fact]
    public void Failed_reconnect_attempt_shows_the_error_with_retrying_wording()
    {
        var banner = ErrorBanner.From(ConnectionState.Disconnected, ErrorKind.PhoneUnreachable, isReconnecting: true)!;

        banner.IsRetryingAutomatically.Should().BeTrue();
        banner.Message.Should().EndWith("DualAudio is retrying automatically.");
        banner.ActionLabel.Should().Be("Retry now");
    }

    [Theory]
    [InlineData(ConnectionState.Idle)]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Opening)]
    public void No_banner_outside_Error_and_Disconnected(ConnectionState state)
    {
        ErrorBanner.From(state, ErrorKind.Unknown, false).Should().BeNull();
    }

    [Fact]
    public void No_banner_without_an_error()
    {
        ErrorBanner.From(ConnectionState.Disconnected, null, false).Should().BeNull();
        ErrorBanner.From(ConnectionState.Error, null, false).Should().BeNull();
    }
}

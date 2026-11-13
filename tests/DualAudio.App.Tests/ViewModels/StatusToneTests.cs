using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DualAudio.App.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Connection;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>Green = connected and receiving; yellow = connected but silent, or connecting/reconnecting; red = not connected.</summary>
public sealed class StatusToneTests
{
    [Theory]
    [InlineData(StatusKind.Receiving, StatusTone.Good)]
    [InlineData(StatusKind.Connected, StatusTone.Warn)]
    [InlineData(StatusKind.Connecting, StatusTone.Warn)]
    [InlineData(StatusKind.Disconnected, StatusTone.Bad)]
    [InlineData(StatusKind.Error, StatusTone.Bad)]
    public void Tone_per_kind(StatusKind kind, StatusTone tone) => StatusTones.For(kind).Should().Be(tone);

    [Fact]
    public void Every_kind_has_a_tone_test()
    {
        Enum.GetValues<StatusKind>().Should().HaveCount(5, "add a Tone_per_kind row for the new kind");
    }

    [Theory]
    [InlineData(ConnectionState.Connected, false, true, false, false, StatusTone.Good)] // receiving
    [InlineData(ConnectionState.Connected, false, true, true, false, StatusTone.Good)] // receiving, muted: still green
    [InlineData(ConnectionState.Connected, false, false, false, false, StatusTone.Warn)] // connected, nothing playing
    [InlineData(ConnectionState.Connected, false, false, true, false, StatusTone.Warn)]
    [InlineData(ConnectionState.Enabling, false, false, false, false, StatusTone.Warn)] // connecting
    [InlineData(ConnectionState.Opening, false, false, false, false, StatusTone.Warn)] // connecting / BUG-001 priming
    [InlineData(ConnectionState.WaitingForPhone, false, false, false, false, StatusTone.Warn)]
    [InlineData(ConnectionState.Disconnected, true, false, false, false, StatusTone.Warn)] // reconnecting
    [InlineData(ConnectionState.Disconnected, false, false, false, false, StatusTone.Bad)]
    [InlineData(ConnectionState.Idle, false, false, false, false, StatusTone.Bad)]
    [InlineData(ConnectionState.Error, false, false, false, false, StatusTone.Bad)] // Bluetooth off, any error
    [InlineData(ConnectionState.Error, false, false, false, true, StatusTone.Bad)] // phone not found
    public void Tone_per_connection_state(ConnectionState state, bool reconnecting, bool receiving, bool muted, bool gaveUp, StatusTone tone)
    {
        DashboardStatus.From(state, reconnecting, receiving, muted, gaveUp).Tone.Should().Be(tone);
    }

    [Fact]
    public void Every_connection_state_is_covered()
    {
        Enum.GetValues<ConnectionState>().Should().BeEquivalentTo(
            [ConnectionState.Idle, ConnectionState.Enabling, ConnectionState.WaitingForPhone, ConnectionState.Opening,
             ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Error],
            "add a Tone_per_connection_state row for the new state");
    }

    [Fact]
    public void Converter_maps_each_tone_to_its_brush()
    {
        var good = new SolidColorBrush(Colors.Green);
        var warn = new SolidColorBrush(Colors.Yellow);
        var bad = new SolidColorBrush(Colors.Red);
        var sut = new StatusToneToBrushConverter { Good = good, Warn = warn, Bad = bad };

        object? Convert(object? value) => sut.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture);

        Convert(StatusTone.Good).Should().BeSameAs(good);
        Convert(StatusTone.Warn).Should().BeSameAs(warn);
        Convert(StatusTone.Bad).Should().BeSameAs(bad);
        Convert(null).Should().Be(DependencyProperty.UnsetValue);
    }
}

using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Bluetooth;
using Windows.Devices.Radios;

namespace DualAudio.Platform.Tests;

public sealed class RadioMonitorAggregateTests
{
    [Fact]
    public void No_radio_is_NotPresent() => RadioMonitor.Aggregate([]).Should().Be(RadioStatus.NotPresent);

    [Theory]
    [InlineData(RadioStatus.On, RadioState.On)]
    [InlineData(RadioStatus.Off, RadioState.Off)]
    [InlineData(RadioStatus.Disabled, RadioState.Disabled)]
    [InlineData(RadioStatus.Unknown, RadioState.Unknown)]
    [InlineData(RadioStatus.On, RadioState.Off, RadioState.On)]
    [InlineData(RadioStatus.Off, RadioState.Disabled, RadioState.Off)]
    public void Any_radio_on_wins_then_off_then_disabled(RadioStatus expected, params RadioState[] states) =>
        RadioMonitor.Aggregate(states).Should().Be(expected);
}

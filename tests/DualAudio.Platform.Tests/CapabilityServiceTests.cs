using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Bluetooth;

namespace DualAudio.Platform.Tests;

public sealed class CapabilityServiceTests
{
    [Fact]
    public async Task PC_name_is_the_NetBIOS_computer_name_that_Windows_advertises_over_Bluetooth()
    {
        var service = new CapabilityService(new RadioMonitor(), new A2dpSourceWatcher());

        var name = await service.GetPcBluetoothNameAsync(CancellationToken.None);

        // Exactly MachineName ("RYELAPTOP"): not the DNS host name ("RyeLaptop") nor the adapter name ("RYELAPTOP- front").
        name.Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task PC_name_lookup_honours_cancellation()
    {
        var service = new CapabilityService(new RadioMonitor(), new A2dpSourceWatcher());

        var act = () => service.GetPcBluetoothNameAsync(new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(19041, true, RadioStatus.On, true)]
    [InlineData(19041, true, RadioStatus.Off, false)]
    [InlineData(18363, false, RadioStatus.On, false)]
    [InlineData(26200, true, RadioStatus.NotPresent, false)]
    public void Continue_needs_a_supported_OS_and_Bluetooth_on(int build, bool supported, RadioStatus radio, bool canContinue)
    {
        new CapabilityReport(build, supported, radio, 0, true).CanContinue.Should().Be(canContinue);
    }
}

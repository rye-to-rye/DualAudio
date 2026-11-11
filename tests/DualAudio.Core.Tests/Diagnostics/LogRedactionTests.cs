using DualAudio.Core.Diagnostics;

namespace DualAudio.Core.Tests.Diagnostics;

public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:aa:bb:cc", "…BBCC")]
    [InlineData("ab", "…AB")]
    [InlineData("zzz", "…")]
    [InlineData("", "<none>")]
    [InlineData(null, "<none>")]
    public void Keeps_only_the_last_four_hex_digits(string? id, string expected)
    {
        LogRedaction.DeviceId(id).Should().Be(expected);
    }

    [Fact]
    public void Never_contains_the_full_address()
    {
        LogRedaction.DeviceId("Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:aa:bb:cc")
            .Should().NotContainAny("5c:f3", "aa:bb", "00:1a");
    }
}

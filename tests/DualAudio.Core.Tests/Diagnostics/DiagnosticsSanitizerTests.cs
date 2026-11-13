using DualAudio.Core.Diagnostics;
using DualAudio.Core.Settings;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Diagnostics;

public sealed class DiagnosticsSanitizerTests
{
    [Theory]
    [InlineData("id Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:aa:bb:cc end", "id Bluetooth#Bluetooth…7113-…BBCC end")]
    [InlineData("addr 5C-F3-70-AA-BB-CC.", "addr …BBCC.")]
    [InlineData(@"BTHENUM\x&0&5CF370AABBCC_C00000000", @"BTHENUM\x&0&…BBCC_C00000000")]
    public void Bluetooth_addresses_keep_only_their_last_4_hex_digits(string input, string expected) =>
        DiagnosticsSanitizer.ScrubBluetoothAddresses(input).Should().Be(expected);

    [Theory]
    [InlineData("%b{2EA30F40-6E9F-4709-9327-A4E880941760}")] // the phone-session GUID: needed in reports
    [InlineData("12:00:01.123 peak=0.127 handles=700")]
    [InlineData("STATE opened: device=…BBCC")]
    public void Ordinary_text_is_left_alone(string text) =>
        DiagnosticsSanitizer.ScrubBluetoothAddresses(text).Should().Be(text);

    [Fact]
    public void Settings_description_has_no_nicknames_and_no_full_device_id()
    {
        var settings = (AppSettings.Default with { PreferredDeviceId = PhoneId, PhoneVolume = 35, PhoneMuted = true })
            .WithNickname(PhoneId, "Ryans iPhone");

        var text = DiagnosticsSanitizer.DescribeSettings(settings);

        text.Should().Contain("PreferredDeviceId: …BBCC")
            .And.Contain("DeviceNicknames: 1 (removed)")
            .And.Contain("PhoneVolume: 35")
            .And.Contain("PhoneMuted: True")
            .And.Contain("Hotkey ToggleMute: Alt, Control + VK 0x4D");
        text.Should().NotContain("Ryans").And.NotContain("5c:f3");
    }
}

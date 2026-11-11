using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;

namespace DualAudio.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_are_sane()
    {
        var s = AppSettings.Default;

        s.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        s.PreferredDeviceId.Should().BeNull();
        s.DeviceNicknames.Should().BeEmpty();
        s.AutoConnectOnStart.Should().BeTrue();
        s.PhoneVolume.Should().Be(100);
        s.PhoneMuted.Should().BeFalse();
        s.StartWithWindows.Should().BeFalse();
        s.Hotkeys.Select(h => h.Action).Should().OnlyHaveUniqueItems();
        s.Hotkeys.Should().OnlyContain(h => h.Modifiers != HotkeyModifiers.None, "bare keys would collide with games");
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(250, 100)]
    public void Sanitize_clamps_volume(int stored, int expected)
    {
        (AppSettings.Default with { PhoneVolume = stored }).Sanitize().PhoneVolume.Should().Be(expected);
    }

    [Fact]
    public void Sanitize_repairs_null_collections_and_blank_device_id()
    {
        var broken = AppSettings.Default with { DeviceNicknames = null!, Hotkeys = null!, PreferredDeviceId = "  " };

        var fixedUp = broken.Sanitize();

        fixedUp.DeviceNicknames.Should().NotBeNull().And.BeEmpty();
        fixedUp.Hotkeys.Should().Equal(AppSettings.DefaultHotkeys);
        fixedUp.PreferredDeviceId.Should().BeNull();
    }

    [Fact]
    public void Sanitize_keeps_the_first_binding_per_action()
    {
        var first = new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Control, 0x4D);
        var duplicate = new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Alt, 0x4E);

        var result = (AppSettings.Default with { Hotkeys = [first, null!, duplicate] }).Sanitize();

        result.Hotkeys.Should().Equal(first);
    }
}

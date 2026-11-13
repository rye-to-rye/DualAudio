using DualAudio.Core.Abstractions;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Settings;

namespace DualAudio.Core.Tests.Hotkeys;

public sealed class HotkeyFormatTests
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    [Fact]
    public void Defaults_are_Ctrl_Alt_M_PgUp_PgDn()
    {
        AppSettings.DefaultHotkeys.Select(HotkeyFormat.Format).Should().Equal("Ctrl+Alt+M", "Ctrl+Alt+PgUp", "Ctrl+Alt+PgDn");
    }

    [Theory]
    [InlineData("Ctrl+Alt+M", CtrlAlt, 0x4D)]
    [InlineData("ctrl + alt + pgup", CtrlAlt, 0x21)]
    [InlineData("Control+Alt+PageDown", CtrlAlt, 0x22)]
    [InlineData("Win+Shift+F9", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x78)]
    [InlineData("Alt+Num5", HotkeyModifiers.Alt, 0x65)]
    [InlineData("Ctrl+Alt+7", CtrlAlt, 0x37)]
    public void Parses_text(string text, HotkeyModifiers modifiers, int key)
    {
        HotkeyFormat.TryParse(text, HotkeyAction.ToggleMute, out var binding).Should().BeTrue();

        binding.Should().Be(new HotkeyBinding(HotkeyAction.ToggleMute, modifiers, key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Ctrl+M")]
    [InlineData("Hyper+M")]
    [InlineData("Ctrl+Alt+NoSuchKey")]
    public void Rejects_bad_text(string? text)
    {
        HotkeyFormat.TryParse(text, HotkeyAction.ToggleMute, out _).Should().BeFalse();
    }

    [Fact]
    public void Every_named_key_round_trips_through_Format_and_TryParse()
    {
        for (var vk = 1; vk < 0xFF; vk++)
        {
            var binding = new HotkeyBinding(HotkeyAction.VolumeUp, CtrlAlt, vk);
            var text = HotkeyFormat.Format(binding);
            if (text.Contains("Key 0x", StringComparison.Ordinal))
            {
                continue; // unnamed key: displayed, not typed
            }

            HotkeyFormat.TryParse(text, HotkeyAction.VolumeUp, out var parsed).Should().BeTrue(text);
            parsed.Should().Be(binding, text);
        }
    }

    [Theory]
    [InlineData(CtrlAlt, 0x4D, true)]
    [InlineData(HotkeyModifiers.Windows, 0x4D, true)]
    [InlineData(HotkeyModifiers.None, 0x4D, false)] // bare key would fire while typing in game chat
    [InlineData(HotkeyModifiers.Shift, 0x4D, false)] // Shift alone types capitals
    [InlineData(CtrlAlt, 0x11, false)] // the Ctrl key itself
    [InlineData(CtrlAlt, 0xA4, false)] // left Alt
    [InlineData(CtrlAlt, 0, false)]
    public void Allowed_combinations(HotkeyModifiers modifiers, int key, bool allowed)
    {
        HotkeyFormat.IsAllowed(new HotkeyBinding(HotkeyAction.ToggleMute, modifiers, key)).Should().Be(allowed);
    }

    [Fact]
    public void Settings_HotkeyFor_and_WithHotkey()
    {
        var custom = new HotkeyBinding(HotkeyAction.VolumeUp, CtrlAlt, 0x26);

        var s = AppSettings.Default.WithHotkey(custom);

        s.HotkeyFor(HotkeyAction.VolumeUp).Should().Be(custom);
        s.HotkeyFor(HotkeyAction.ToggleMute).Should().Be(AppSettings.DefaultHotkeys[0], "other bindings are kept");
        s.Hotkeys.Should().HaveCount(3);
        (AppSettings.Default with { Hotkeys = [] }).HotkeyFor(HotkeyAction.VolumeDown)
            .Should().Be(AppSettings.DefaultHotkeys[2], "a missing binding falls back to the default");
    }
}

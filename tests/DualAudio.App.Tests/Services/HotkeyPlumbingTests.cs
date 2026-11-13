using System.Text.Json;
using System.Windows.Input;
using DualAudio.App.Services;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;
using DualAudio.Infrastructure.Settings;

namespace DualAudio.App.Tests.Services;

public sealed class HotkeyPlumbingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DualAudioTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Theory]
    [InlineData(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x0002u | 0x0001u | 0x4000u)] // mute: MOD_NOREPEAT
    [InlineData(HotkeyAction.VolumeUp, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x0002u | 0x0001u)] // volume may auto-repeat
    [InlineData(HotkeyAction.VolumeDown, HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x0004u | 0x0008u)]
    public void Modifiers_map_to_Win32_MOD_flags(HotkeyAction action, HotkeyModifiers modifiers, uint expected)
    {
        Win32HotkeyService.ToNativeModifiers(new HotkeyBinding(action, modifiers, 0x4D)).Should().Be(expected);
    }

    [Fact]
    public void Hotkey_ids_are_distinct_and_in_the_application_range()
    {
        var ids = Enum.GetValues<HotkeyAction>().Select(Win32HotkeyService.IdFor).ToList();

        ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id >= 0 && id <= 0xBFFF);
    }

    [Fact]
    public void Recorder_maps_Ctrl_Alt_PageUp_including_the_Alt_SystemKey_case()
    {
        HotkeyKeyMapper.TryMap(Key.System, Key.PageUp, ModifierKeys.Control | ModifierKeys.Alt, out var mods, out var vk).Should().BeTrue();

        mods.Should().Be(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        vk.Should().Be(0x21);
    }

    [Fact]
    public void Recorder_waits_while_only_modifiers_are_held()
    {
        HotkeyKeyMapper.TryMap(Key.LeftCtrl, Key.None, ModifierKeys.Control, out _, out _).Should().BeFalse();
        HotkeyKeyMapper.TryMap(Key.System, Key.LeftAlt, ModifierKeys.Alt, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Hotkeys_are_stored_readably_and_round_trip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var custom = AppSettings.Default.WithHotkey(new HotkeyBinding(HotkeyAction.VolumeUp, HotkeyModifiers.Control | HotkeyModifiers.Windows, 0x26));
        using (var writer = new JsonSettingsStore(path))
        {
            await writer.SaveAsync(custom, CancellationToken.None);
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var stored = doc.RootElement.GetProperty("hotkeys").EnumerateArray()
            .Single(h => h.GetProperty("action").GetString() == "VolumeUp");
        stored.GetProperty("modifiers").GetString().Should().Be("Control, Windows");
        stored.GetProperty("virtualKey").GetInt32().Should().Be(0x26);

        using var reader = new JsonSettingsStore(path);
        (await reader.LoadAsync(CancellationToken.None)).Hotkeys.Should().BeEquivalentTo(custom.Hotkeys);
    }
}

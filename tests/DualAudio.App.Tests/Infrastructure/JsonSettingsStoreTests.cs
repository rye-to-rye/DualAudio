using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;
using DualAudio.Infrastructure.Settings;

namespace DualAudio.App.Tests.Infrastructure;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DualAudioTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_file_loads_defaults()
    {
        using var store = new JsonSettingsStore(SettingsPath);

        var settings = await store.LoadAsync(CancellationToken.None);

        settings.Should().BeEquivalentTo(AppSettings.Default);
    }

    [Fact]
    public async Task Round_trip_keeps_every_field()
    {
        var saved = AppSettings.Default with
        {
            PreferredDeviceId = "Bluetooth#Bluetooth00:00:00:00:00:00-00:00:00:00:12:34",
            DeviceNicknames = new Dictionary<string, string> { ["id-1"] = "Work phone" },
            AutoConnectOnStart = false,
            PhoneVolume = 37,
            PhoneMuted = true,
            Hotkeys = [new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4D)],
            StartWithWindows = true,
            OnboardingCompleted = true,
            TrayNoticeShown = true,
        };

        using (var writer = new JsonSettingsStore(SettingsPath))
        {
            await writer.SaveAsync(saved, CancellationToken.None);
        }

        using var reader = new JsonSettingsStore(SettingsPath);
        var loaded = await reader.LoadAsync(CancellationToken.None);

        loaded.Should().BeEquivalentTo(saved);
    }

    [Fact]
    public async Task Save_overwrites_atomically_and_leaves_no_temp_file()
    {
        using var store = new JsonSettingsStore(SettingsPath);

        await store.SaveAsync(AppSettings.Default with { PhoneVolume = 10 }, CancellationToken.None);
        await store.SaveAsync(AppSettings.Default with { PhoneVolume = 90 }, CancellationToken.None);

        (await store.LoadAsync(CancellationToken.None)).PhoneVolume.Should().Be(90);
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("settings.json");
    }

    [Fact]
    public async Task Corrupt_file_is_backed_up_and_defaults_are_loaded()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SettingsPath, "{ \"phoneVolume\": 40, this is not json");
        using var store = new JsonSettingsStore(SettingsPath);

        var settings = await store.LoadAsync(CancellationToken.None);

        settings.Should().BeEquivalentTo(AppSettings.Default);
        File.Exists(SettingsPath).Should().BeFalse("the corrupt file was moved aside");
        var backup = Directory.GetFiles(_dir, "settings.json.corrupt-*.bak").Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(backup)).Should().Contain("this is not json");

        await store.SaveAsync(settings with { PhoneVolume = 55 }, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).PhoneVolume.Should().Be(55);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Json_that_is_not_a_settings_object_counts_as_corrupt(string content)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SettingsPath, content);
        using var store = new JsonSettingsStore(SettingsPath);

        (await store.LoadAsync(CancellationToken.None)).Should().BeEquivalentTo(AppSettings.Default);
        Directory.GetFiles(_dir, "settings.json.corrupt-*.bak").Should().ContainSingle();
    }

    [Fact]
    public async Task Out_of_range_values_are_sanitized_on_load()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SettingsPath, """{ "phoneVolume": 250, "preferredDeviceId": "  " }""");
        using var store = new JsonSettingsStore(SettingsPath);

        var settings = await store.LoadAsync(CancellationToken.None);

        settings.PhoneVolume.Should().Be(100);
        settings.PreferredDeviceId.Should().BeNull();
        settings.Hotkeys.Should().BeEquivalentTo(AppSettings.DefaultHotkeys);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        using var store = new JsonSettingsStore(SettingsPath);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.SaveAsync(AppSettings.Default with { PhoneVolume = i }, CancellationToken.None))));

        (await store.LoadAsync(CancellationToken.None)).PhoneVolume.Should().BeInRange(0, 19);
        Directory.GetFiles(_dir, "*.bak").Should().BeEmpty();
    }
}

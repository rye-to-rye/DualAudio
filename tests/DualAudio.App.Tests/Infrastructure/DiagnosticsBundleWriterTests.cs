using System.IO.Compression;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Settings;
using DualAudio.Infrastructure;
using DualAudio.Infrastructure.Diagnostics;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.Infrastructure;

/// <summary>Prompt 9 Part D: the bug-report zip has the listed contents and nothing private.</summary>
public sealed class DiagnosticsBundleWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualAudioTests", Guid.NewGuid().ToString("N"));
    private readonly string _desktop;
    private readonly AppPaths _paths;

    public DiagnosticsBundleWriterTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "DualAudio"));
        _desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(_paths.LogDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Zip_has_the_newest_3_logs_and_the_report_and_nothing_else()
    {
        WriteLog("dualaudio-20261110.log", "old day", daysAgo: 3);
        WriteLog("dualaudio-20261111.log", "day 2", daysAgo: 2);
        WriteLog("dualaudio-20261112.log", "day 3", daysAgo: 1);
        WriteLog("dualaudio-20261113.log", "today", daysAgo: 0);
        File.WriteAllText(Path.Combine(_paths.LogDirectory, "notes.txt"), "not a log");
        File.WriteAllText(_paths.SettingsFile, "{ \"secret\": true }");

        var path = await new DiagnosticsBundleWriter(_paths).WriteAsync(_desktop, Bundle(), CancellationToken.None);

        Path.GetDirectoryName(path).Should().Be(_desktop);
        Path.GetFileName(path).Should().Be("DualAudio-diagnostics-20261113-120000.zip");
        using var zip = ZipFile.OpenRead(path);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            "diagnostics.txt", "logs/dualaudio-20261111.log", "logs/dualaudio-20261112.log", "logs/dualaudio-20261113.log");
    }

    [Fact]
    public async Task Report_lists_version_Windows_adapter_driver_outputs_settings_and_STATE_lines()
    {
        var path = await new DiagnosticsBundleWriter(_paths).WriteAsync(_desktop, Bundle(), CancellationToken.None);

        var report = Read(path, "diagnostics.txt");
        report.Should().Contain("App version:     1.2.3")
            .And.Contain("Windows:         10.0.26200.7171 (25H2)")
            .And.Contain("Intel(R) Wireless Bluetooth(R) | driver 23.60.0.1 | provider Intel Corporation | date 2026-05-01")
            .And.Contain("Headset Earphone (Cirrus Logic) (default)")
            .And.Contain("Speakers (fifine Microphone)")
            .And.Contain("RESOURCES interval: cpu=0.05%")
            .And.Contain("PhoneVolume: 35")
            .And.Contain("Last 2 STATE lines:")
            .And.Contain("STATE opened+10s: coord=Connected");
    }

    [Fact]
    public async Task Nothing_private_full_addresses_nicknames_and_the_profile_path_are_removed()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        WriteLog(
            "dualaudio-20261113.log",
            "Connect requested for Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:aa:bb:cc\n" +
            @"BTHENUM\{0000110a}_LOCALMFG&0002\7&1a2b&0&5CF370AABBCC_C00000000" + "\n" +
            "Nickname Ryans iPhone saved\n" +
            $"Log folder {profile}\\AppData\\Local\\DualAudio\\logs\n",
            daysAgo: 0);

        var path = await new DiagnosticsBundleWriter(_paths).WriteAsync(_desktop, Bundle(), CancellationToken.None);

        var everything = Read(path, "diagnostics.txt") + Read(path, "logs/dualaudio-20261113.log");
        everything.Should().NotContainAny("5c:f3:70:aa:bb:cc", "00:1a:7d:da:71:13", "5CF370AABBCC", "Ryans iPhone", profile);
        everything.Should().Contain("…BBCC").And.Contain("<nickname>").And.Contain("%USERPROFILE%");
        Read(path, "diagnostics.txt").Should().Contain("PreferredDeviceId: …BBCC").And.Contain("DeviceNicknames: 1 (removed)");
    }

    [Fact]
    public async Task A_second_zip_in_the_same_second_gets_its_own_name()
    {
        var writer = new DiagnosticsBundleWriter(_paths);

        var first = await writer.WriteAsync(_desktop, Bundle(), CancellationToken.None);
        var second = await writer.WriteAsync(_desktop, Bundle(), CancellationToken.None);

        second.Should().NotBe(first).And.EndWith("-2.zip");
    }

    [Fact]
    public async Task Todays_log_can_be_read_while_the_logger_holds_it_open()
    {
        var today = Path.Combine(_paths.LogDirectory, "dualaudio-20261113.log");
        await using var held = new FileStream(today, FileMode.Create, FileAccess.Write, FileShare.Read);
        await held.WriteAsync("still writing"u8.ToArray());
        await held.FlushAsync();

        var path = await new DiagnosticsBundleWriter(_paths).WriteAsync(_desktop, Bundle(), CancellationToken.None);

        Read(path, "logs/dualaudio-20261113.log").Should().Be("still writing");
    }

    private static DiagnosticsBundle Bundle() => new(
        "1.2.3",
        new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero),
        new SystemInfo("10.0.26200.7171 (25H2)", [new BluetoothAdapterInfo("Intel(R) Wireless Bluetooth(R)", "Intel Corporation", "23.60.0.1", "2026-05-01")]),
        ["Headset Earphone (Cirrus Logic) (default)", "Speakers (fifine Microphone)"],
        (AppSettings.Default with { PreferredDeviceId = PhoneId, PhoneVolume = 35 }).WithNickname(PhoneId, "Ryans iPhone"),
        ["2026-11-13 11:59:50.000Z STATE opened: coord=Connected", "2026-11-13 11:59:59.000Z STATE opened+10s: coord=Connected"],
        "RESOURCES interval: cpu=0.05% private=80.0MB handles=700 gdi=40 user=30 threads=30",
        ["Ryans iPhone"]);

    private static string Read(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    private void WriteLog(string name, string content, int daysAgo)
    {
        var path = Path.Combine(_paths.LogDirectory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo));
    }
}

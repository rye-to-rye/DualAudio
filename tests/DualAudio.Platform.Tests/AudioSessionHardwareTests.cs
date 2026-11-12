using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Platform.Windows.Audio;
using DualAudio.Platform.Windows.Bluetooth;
using NAudio.CoreAudioApi;

namespace DualAudio.Platform.Tests;

/// <summary>
/// Real Core Audio checks. Run with DUALAUDIO_HW_TESTS=1, Bluetooth on, phone paired, unlocked and in range
/// (DUALAUDIO_HW_DEVICE picks the phone by name). The test connects the phone itself; close the probe first.
/// Read-back uses NAudio directly, independently of the controller under test.
/// </summary>
[Trait("Category", "Hardware")]
[Collection("Hardware")]
public sealed class AudioSessionHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task Phone_volume_30_reaches_every_phone_session_and_no_other_session()
    {
        using var watcher = new A2dpSourceWatcher(new TestOutputLogger<A2dpSourceWatcher>(output));
        using var receiver = new PlaybackConnectionReceiver(new TestOutputLogger<PlaybackConnectionReceiver>(output));
        using var controller = new NAudioSessionController(new TestOutputLogger<NAudioSessionController>(output));
        var phone = await ConnectAsync(watcher, receiver);

        try
        {
            var before = await Task.Run(ReadAllSessions);
            var phoneBefore = before.Where(s => s.IsPhone).ToList();
            var othersBefore = before.Where(s => !s.IsPhone).ToList();
            output.WriteLine($"Before: {phoneBefore.Count} phone session(s), {othersBefore.Count} other session(s)");
            phoneBefore.Should().NotBeEmpty("connected: Windows creates one phone session per render endpoint");

            try
            {
                await controller.SetPhoneVolumeAsync(30, CancellationToken.None);

                var after = await Task.Run(ReadAllSessions);
                var phoneAfter = after.Where(s => s.IsPhone).ToList();
                phoneAfter.Should().HaveCount(phoneBefore.Count);
                foreach (var s in phoneAfter)
                {
                    output.WriteLine($"Phone session on \"{s.EndpointName}\": {s.Volume:0.00}");
                    s.Volume.Should().BeApproximately(0.30f, 0.005f, $"phone session on \"{s.EndpointName}\"");
                }

                foreach (var other in othersBefore)
                {
                    var now = after.FirstOrDefault(a => a.Key == other.Key);
                    if (now is null)
                    {
                        continue; // that app's session ended meanwhile
                    }

                    now.Volume.Should().Be(other.Volume, $"non-phone session pid {other.ProcessId} on \"{other.EndpointName}\" must be unchanged");
                    now.Mute.Should().Be(other.Mute);
                }

                var status = await controller.GetPhoneAudioStatusAsync(CancellationToken.None);
                status.Available.Should().BeTrue();
                status.Sessions.Should().OnlyContain(s => s.VolumePercent == 30);
                output.WriteLine($"Status: active on default={status.IsActiveOnDefault}, peak={status.Peak:0.000}");
            }
            finally
            {
                // Restore what the phone sessions had (they were all equal or the user set them; use the default's).
                var restore = phoneBefore.FirstOrDefault(s => s.IsOnDefault) ?? phoneBefore.FirstOrDefault();
                if (restore is not null)
                {
                    await controller.SetPhoneVolumeAsync((int)Math.Round(restore.Volume * 100), CancellationToken.None);
                }
            }
        }
        finally
        {
            await receiver.CloseAsync();
            output.WriteLine($"Disconnected \"{phone.Name}\"");
        }
    }

    [HardwareFact]
    public async Task Output_device_service_lists_active_endpoints_with_one_default()
    {
        using var service = new OutputDeviceService(new TestOutputLogger<OutputDeviceService>(output));

        var endpoints = await service.GetRenderEndpointsAsync(CancellationToken.None);
        var defaultEndpoint = await service.GetDefaultEndpointAsync(CancellationToken.None);

        foreach (var e in endpoints)
        {
            output.WriteLine($"\"{e.Name}\"{(e.IsDefault ? " [DEFAULT]" : string.Empty)}");
        }

        endpoints.Should().NotBeEmpty();
        endpoints.Count(e => e.IsDefault).Should().Be(1);
        defaultEndpoint.Should().NotBeNull();
        endpoints.Single(e => e.IsDefault).Id.Should().Be(defaultEndpoint!.Id);
    }

    private async Task<PhoneDevice> ConnectAsync(A2dpSourceWatcher watcher, PlaybackConnectionReceiver receiver)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.EnumerationCompleted += (_, _) => completed.TrySetResult();
        watcher.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var filter = Environment.GetEnvironmentVariable("DUALAUDIO_HW_DEVICE");
        var phone = string.IsNullOrWhiteSpace(filter)
            ? watcher.Devices.FirstOrDefault()
            : watcher.Devices.FirstOrDefault(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        phone.Should().NotBeNull("a paired phone is required (DUALAUDIO_HW_DEVICE filters by name)");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await receiver.OpenAsync(phone!.Id, timeout.Token);
        result.Success.Should().BeTrue($"{result.ErrorKind} ({result.Detail})");
        output.WriteLine($"Connected \"{phone.Name}\"");

        // Give Windows a moment to create the per-endpoint sessions.
        for (var i = 0; i < 20 && !(await Task.Run(ReadAllSessions)).Any(s => s.IsPhone); i++)
        {
            await Task.Delay(500);
        }

        return phone;
    }

    /// <summary>Independent read of every session on every active render endpoint (MTA pool thread).</summary>
    private static List<SessionReading> ReadAllSessions()
    {
        var list = new List<SessionReading>();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var def) && def is not null)
        {
            using (def)
            {
                defaultId = def.ID;
            }
        }

        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var d = 0; d < devices.Count; d++)
        {
            using var device = devices[d];
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var s = sessions[i];
                var pid = (int)s.GetProcessID;
                string processName;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    processName = p.ProcessName;
                }
                catch (Exception)
                {
                    processName = string.Empty;
                }

                var info = new PhoneSessionInfo(device.ID, processName, pid, s.DisplayName, s.GetSessionIdentifier, s.GetSessionInstanceIdentifier, s.IsSystemSoundsSession);
                list.Add(new SessionReading(
                    device.ID + "|" + info.SessionInstanceIdentifier,
                    device.FriendlyName,
                    device.ID == defaultId,
                    pid,
                    PhoneSessionMatcher.IsPhoneSession(info),
                    s.SimpleAudioVolume.Volume,
                    s.SimpleAudioVolume.Mute));
            }
        }

        return list;
    }

    private sealed record SessionReading(string Key, string EndpointName, bool IsOnDefault, int ProcessId, bool IsPhone, float Volume, bool Mute);
}

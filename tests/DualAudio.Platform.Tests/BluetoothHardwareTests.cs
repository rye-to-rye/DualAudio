using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Bluetooth;

namespace DualAudio.Platform.Tests;

/// <summary>
/// Real-hardware checks. Run with DUALAUDIO_HW_TESTS=1, Bluetooth on, phone paired, unlocked and in range.
/// Optional DUALAUDIO_HW_DEVICE=(part of the phone's name) picks the phone when several are paired.
/// </summary>
[Trait("Category", "Hardware")]
[Collection("Hardware")]
public sealed class BluetoothHardwareTests(ITestOutputHelper output)
{
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(20);

    [HardwareFact]
    public async Task Watcher_finds_at_least_one_device()
    {
        using var watcher = new A2dpSourceWatcher(new TestOutputLogger<A2dpSourceWatcher>(output));

        var devices = await EnumerateAsync(watcher);

        devices.Should().NotBeEmpty("a phone must be paired in Settings > Bluetooth & devices");
        watcher.IsEnumerationCompleted.Should().BeTrue();
    }

    [HardwareFact]
    public async Task Connect_disconnect_cycle_5_times()
    {
        using var watcher = new A2dpSourceWatcher(new TestOutputLogger<A2dpSourceWatcher>(output));
        using var receiver = new PlaybackConnectionReceiver(new TestOutputLogger<PlaybackConnectionReceiver>(output));
        var phone = PickPhone(await EnumerateAsync(watcher));
        output.WriteLine($"Using \"{phone.Name}\"");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnState(object? sender, ReceiverStateChangedEventArgs e)
            {
                if (e.State == ReceiverState.Opened && e.DeviceId == phone.Id)
                {
                    opened.TrySetResult();
                }
            }

            receiver.StateChanged += OnState;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await receiver.OpenAsync(phone.Id, timeout.Token);
                result.Success.Should().BeTrue($"attempt {attempt}: {result.ErrorKind} ({result.Detail})");

                // Phase 0: Opened arrives via StateChanged ~1 ms after OpenAsync; State may still read Closed.
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                receiver.StateChanged -= OnState;
            }

            await receiver.CloseAsync();
            receiver.State.Should().Be(ReceiverState.Closed, $"attempt {attempt}: CloseAsync disposes the connection");
            output.WriteLine($"Cycle {attempt}/5 OK");

            await Task.Delay(TimeSpan.FromSeconds(2)); // let the phone settle between cycles
        }
    }

    [HardwareFact]
    public async Task Radio_status_is_read()
    {
        using var radio = new RadioMonitor(new TestOutputLogger<RadioMonitor>(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await radio.StartAsync(timeout.Token);

        output.WriteLine($"Radio status: {radio.Status}");
        radio.Status.Should().NotBe(RadioStatus.Unknown);
    }

    private static async Task<IReadOnlyList<PhoneDevice>> EnumerateAsync(A2dpSourceWatcher watcher)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.EnumerationCompleted += (_, _) => completed.TrySetResult();
        watcher.Start();
        await completed.Task.WaitAsync(EnumerationTimeout);
        return watcher.Devices;
    }

    private static PhoneDevice PickPhone(IReadOnlyList<PhoneDevice> devices)
    {
        var filter = Environment.GetEnvironmentVariable("DUALAUDIO_HW_DEVICE");
        var phone = string.IsNullOrWhiteSpace(filter)
            ? devices.FirstOrDefault()
            : devices.FirstOrDefault(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        phone.Should().NotBeNull("a paired phone is required (DUALAUDIO_HW_DEVICE filters by name)");
        return phone!;
    }
}

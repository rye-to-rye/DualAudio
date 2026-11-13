using System.Diagnostics;
using DualAudio.Core.Diagnostics;
using DualAudio.Platform.Windows.Audio;
using DualAudio.Platform.Windows.Bluetooth;
using DualAudio.Platform.Windows.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DualAudio.Probe;

/// <summary>
/// <c>stress &lt;n&gt; [index|sessions]</c>: n connect/disconnect cycles through the app's own
/// <see cref="PlaybackConnectionReceiver"/> plus n full session enumerations through <see cref="NAudioSessionController"/>
/// (the code paths that hold COM/WinRT objects), then compares handles, GDI/USER objects and private bytes.
/// One warm-up cycle runs first so one-time allocations (MTA worker thread, activation factories) don't count as growth.
/// </summary>
internal sealed class StressCommand(ProbeLog log)
{
    /// <summary>Hardware: a reopen 1 s after close failed with 0x8007001F; >= 3 s always worked.</summary>
    private static readonly TimeSpan GapAfterClose = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OpenedWait = TimeSpan.FromSeconds(3);
    private const double MaxHandleGrowthPercent = 5.0;

    private readonly ProcessResourceReader _reader = new();

    /// <param name="deviceId">Phone to cycle; null = session enumerations only.</param>
    public async Task RunAsync(int cycles, string? deviceId, string? deviceLabel, CancellationToken token)
    {
        log.Info(deviceId is null
            ? $"stress: {cycles} session enumerations (no Bluetooth cycles)"
            : $"stress: {cycles} connect/disconnect cycles with {deviceLabel} + {cycles} session enumerations (~{(int)(cycles * 7 / 60.0) + 1} min)");

        using var loggers = LoggerFactory.Create(b => b.AddProvider(new ProbeLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning));
        var before = await MeasureAsync();

        var receiver = new PlaybackConnectionReceiver(loggers.CreateLogger<PlaybackConnectionReceiver>());
        var sessions = new NAudioSessionController(loggers.CreateLogger<NAudioSessionController>());
        var opened = 0;
        var failed = 0;
        var phoneSessions = 0;
        var sw = Stopwatch.StartNew();
        ProcessResourceSnapshot baseline;
        ProcessResourceSnapshot alive;
        try
        {
            // Warm-up (not counted).
            if (deviceId is not null)
            {
                await CycleAsync(receiver, deviceId, token);
            }

            await sessions.GetPhoneSessionsAsync(token);
            baseline = await MeasureAsync();

            for (var i = 1; i <= cycles; i++)
            {
                token.ThrowIfCancellationRequested();
                if (deviceId is not null)
                {
                    if (await CycleAsync(receiver, deviceId, token))
                    {
                        opened++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                phoneSessions = (await sessions.GetPhoneSessionsAsync(token)).Count;
                if (i % 10 == 0 || i == cycles)
                {
                    var now = _reader.Read();
                    log.Info($"stress {i}/{cycles}: opened {opened}, failed {failed}, phone sessions {phoneSessions}, " +
                             $"handles {now.HandleCount}, private {Mb(now.PrivateBytes)} ({sw.Elapsed:mm\\:ss})");
                }
            }

            alive = await MeasureAsync();
        }
        finally
        {
            await receiver.CloseAsync();
            receiver.Dispose();
            sessions.Dispose();
        }

        var disposed = await MeasureAsync();
        Report(before, baseline, alive, disposed, opened, failed, deviceId is not null);
    }

    /// <summary>Open (StartAsync + OpenAsync), wait briefly for Opened, close, then the gap the hardware needs.</summary>
    private async Task<bool> CycleAsync(PlaybackConnectionReceiver receiver, string deviceId, CancellationToken token)
    {
        var openedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(object? sender, Core.Abstractions.ReceiverStateChangedEventArgs e)
        {
            if (e.State == Core.Abstractions.ReceiverState.Opened)
            {
                openedEvent.TrySetResult();
            }
        }

        receiver.StateChanged += OnState;
        var ok = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(OpenTimeout);
            var result = await receiver.OpenAsync(deviceId, timeout.Token);
            if (result.Success)
            {
                await Task.WhenAny(openedEvent.Task, Task.Delay(OpenedWait, token));
                ok = true;
            }
            else
            {
                log.Warn($"stress: open failed: {result.ErrorKind} ({result.Detail})");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log.Warn("stress: open timed out");
        }
        finally
        {
            receiver.StateChanged -= OnState;
            await receiver.CloseAsync();
        }

        await Task.Delay(GapAfterClose, token);
        return ok;
    }

    private async Task<ProcessResourceSnapshot> MeasureAsync()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(500); // let finalizer-released COM objects and exiting threads settle
        return _reader.Read();
    }

    private void Report(
        ProcessResourceSnapshot before,
        ProcessResourceSnapshot baseline,
        ProcessResourceSnapshot alive,
        ProcessResourceSnapshot disposed,
        int opened,
        int failed,
        bool cycled)
    {
        static string Row(string label, ProcessResourceSnapshot s) =>
            $"  {label,-26} handles {s.HandleCount,6}  gdi {s.GdiObjects,5}  user {s.UserObjects,5}  threads {s.ThreadCount,4}  private {Mb(s.PrivateBytes),10}";

        log.Info("stress results (after GC):");
        log.Info(Row("before", before));
        log.Info(Row("baseline (after warm-up)", baseline));
        log.Info(Row("after cycles (alive)", alive));
        log.Info(Row("after dispose", disposed));
        if (cycled)
        {
            log.Info($"  connect cycles: {opened} opened, {failed} failed");
        }

        var growth = baseline.HandleCount == 0 ? 0 : (alive.HandleCount - baseline.HandleCount) * 100.0 / baseline.HandleCount;
        var gdiUserGrowth = (alive.GdiObjects - baseline.GdiObjects) + (alive.UserObjects - baseline.UserObjects);
        var memoryGrowth = baseline.PrivateBytes == 0 ? 0 : (alive.PrivateBytes - baseline.PrivateBytes) * 100.0 / baseline.PrivateBytes;
        var line = $"  handle growth {growth:+0.0;-0.0}% (limit {MaxHandleGrowthPercent}%), GDI+USER {gdiUserGrowth:+0;-0}, private bytes {memoryGrowth:+0.0;-0.0}%";
        // A one-off object (e.g. a hidden COM window created on first use) is not a leak; growth with n is.
        var gdiUserLimit = Math.Max(5, (baseline.GdiObjects + baseline.UserObjects) * MaxHandleGrowthPercent / 100);
        if (growth < MaxHandleGrowthPercent && gdiUserGrowth <= gdiUserLimit)
        {
            log.Ok(line + "  -> PASS");
        }
        else
        {
            log.Error(line + "  -> FAIL (run again to rule out noise; note the numbers in docs/test-log.md)");
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";
}

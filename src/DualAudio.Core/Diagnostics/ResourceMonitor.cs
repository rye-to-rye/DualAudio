using System.Globalization;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Diagnostics;

/// <summary>One reading of this process's resource use.</summary>
/// <param name="TotalProcessorTime">CPU time used since the process started (all threads).</param>
/// <param name="PrivateBytes">Private memory (commit) in bytes.</param>
/// <param name="HandleCount">Kernel handles.</param>
/// <param name="GdiObjects">GDI objects (Windows); -1 when unknown.</param>
/// <param name="UserObjects">USER objects (Windows); -1 when unknown.</param>
public sealed record ProcessResourceSnapshot(
    TimeSpan TotalProcessorTime,
    long PrivateBytes,
    int HandleCount,
    int GdiObjects,
    int UserObjects,
    int ThreadCount);

/// <summary>Reads <see cref="ProcessResourceSnapshot"/> for the current process (Platform: Process + GetGuiResources).</summary>
public interface IProcessResources
{
    ProcessResourceSnapshot Read();
}

public sealed record ResourceMonitorOptions
{
    /// <summary>How often a RESOURCES line is logged. Never below 10 s (performance budget: no fast polling).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Budget: average CPU over an interval, percent of the whole machine.</summary>
    public double CpuBudgetPercent { get; init; } = 0.5;

    /// <summary>Budget: private memory.</summary>
    public long PrivateBytesBudget { get; init; } = 120L * 1024 * 1024;
}

/// <summary>
/// Logs one <c>RESOURCES</c> line every <see cref="ResourceMonitorOptions.Interval"/> (default 10 min): average CPU since
/// the previous line, private memory, handles, GDI/USER objects, threads; a warning when over budget. Lets a long game
/// session be checked against the budget from the log alone. Never throws.
/// </summary>
public sealed class ResourceMonitor : IAsyncDisposable
{
    public const string LinePrefix = "RESOURCES";
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    private readonly IProcessResources _resources;
    private readonly IClock _clock;
    private readonly ResourceMonitorOptions _options;
    private readonly ILogger _logger;
    private readonly int _processorCount;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private (DateTimeOffset At, ProcessResourceSnapshot Snapshot)? _previous;
    private string? _latestLine;
    private int _started;

    public ResourceMonitor(
        IProcessResources resources,
        IClock clock,
        ResourceMonitorOptions? options = null,
        ILogger<ResourceMonitor>? logger = null,
        int? processorCount = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(clock);
        _resources = resources;
        _clock = clock;
        var o = options ?? new ResourceMonitorOptions();
        _options = o.Interval < MinInterval ? o with { Interval = MinInterval } : o;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    /// <summary>The effective interval (after clamping to at least 10 s).</summary>
    public TimeSpan Interval => _options.Interval;

    /// <summary>The most recent line (for "Copy diagnostics"); written now if none exists yet.</summary>
    public string LatestLine
    {
        get
        {
            lock (_sync)
            {
                return _latestLine ?? Measure("now");
            }
        }
    }

    /// <summary>Logs a first line (baseline) now, then one per interval. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        lock (_sync)
        {
            Measure("start");
        }

        _ = LoopAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync() => await _lifetime.CancelAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                await _clock.Delay(_options.Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sync)
            {
                Measure("interval");
            }
        }
    }

    /// <summary>Under _sync.</summary>
    private string Measure(string why)
    {
        ProcessResourceSnapshot snapshot;
        try
        {
            snapshot = _resources.Read();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading process resources failed");
            return _latestLine ?? $"{LinePrefix} unavailable";
        }

        var now = _clock.UtcNow;
        double? cpu = null;
        if (_previous is { } previous && now > previous.At)
        {
            var wall = (now - previous.At).TotalMilliseconds * _processorCount;
            cpu = Math.Max(0, (snapshot.TotalProcessorTime - previous.Snapshot.TotalProcessorTime).TotalMilliseconds) / wall * 100;
        }

        _previous = (now, snapshot);
        var line = Format(why, cpu, snapshot);
        _latestLine = line;

        var overCpu = cpu > _options.CpuBudgetPercent;
        var overMemory = snapshot.PrivateBytes > _options.PrivateBytesBudget;
        if (overCpu || overMemory)
        {
            _logger.LogWarning(
                "{Line} OVER BUDGET ({Which}; budget cpu {CpuBudget}% private {MemBudget} MB)",
                line,
                overCpu && overMemory ? "cpu+memory" : overCpu ? "cpu" : "memory",
                _options.CpuBudgetPercent.ToString(CultureInfo.InvariantCulture),
                _options.PrivateBytesBudget / (1024 * 1024));
        }
        else
        {
            _logger.LogInformation("{Line}", line);
        }

        return line;
    }

    internal static string Format(string why, double? cpuPercent, ProcessResourceSnapshot s) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{LinePrefix} {why}: cpu={(cpuPercent is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "-")} " +
            $"private={s.PrivateBytes / (1024.0 * 1024.0):0.0}MB handles={s.HandleCount} gdi={s.GdiObjects} user={s.UserObjects} threads={s.ThreadCount}");
}

using DualAudio.Core.Updates;

namespace DualAudio.Core.Tests.Fakes;

/// <summary>An <see cref="IAppUpdater"/> without network: tests decide what a check finds and when a download finishes.</summary>
internal sealed class FakeAppUpdater : IAppUpdater
{
    private readonly object _sync = new();
    private readonly List<(AvailableUpdate Update, bool Restart)> _applied = [];

    public bool IsInstalled { get; set; } = true;

    public string? CurrentVersion { get; set; } = "0.1.0";

    public AvailableUpdate? PendingRestart { get; set; }

    /// <summary>What the next checks find (null = up to date).</summary>
    public AvailableUpdate? Available { get; set; }

    /// <summary>When set, checks throw this (offline, no release, rate limit…).</summary>
    public Exception? CheckFailure { get; set; }

    /// <summary>When set, downloads complete only when this completes.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public IReadOnlyList<(AvailableUpdate Update, bool Restart)> Applied
    {
        get { lock (_sync) { return [.. _applied]; } }
    }

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return CheckFailure is { } failure ? Task.FromException<AvailableUpdate?>(failure) : Task.FromResult(Available);
    }

    public Task DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken)
    {
        Downloads++;
        return DownloadGate?.Task ?? Task.CompletedTask;
    }

    public void ApplyAfterExit(AvailableUpdate update, bool restart)
    {
        lock (_sync)
        {
            _applied.Add((update, restart));
        }
    }
}

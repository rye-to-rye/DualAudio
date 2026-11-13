using DualAudio.Core.Diagnostics;
using DualAudio.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;

namespace DualAudio.Core.Tests.Diagnostics;

public sealed class ResourceMonitorTests
{
    private readonly FakeClock _clock = new();
    private readonly ListLogger _log = new();
    private readonly IProcessResources _resources = Substitute.For<IProcessResources>();

    [Fact]
    public async Task Logs_a_baseline_then_one_line_every_10_minutes_with_average_CPU()
    {
        _resources.Read().Returns(
            Snapshot(cpu: TimeSpan.Zero, privateMb: 80),
            Snapshot(cpu: TimeSpan.FromSeconds(1.2), privateMb: 82)); // 1.2 s CPU over 10 min on 2 cores = 0.10 %
        await using var sut = new ResourceMonitor(_resources, _clock, logger: _log, processorCount: 2);

        sut.Start();
        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        _log.Lines.Should().ContainSingle().Which.Should().StartWith("RESOURCES start: cpu=- private=80.0MB");

        _clock.Advance(TimeSpan.FromSeconds(1));
        _log.Lines.Should().HaveCount(2);
        _log.Lines[1].Should().Be("RESOURCES interval: cpu=0.10% private=82.0MB handles=700 gdi=40 user=30 threads=25");
        sut.LatestLine.Should().Be(_log.Lines[1]);
    }

    [Fact]
    public async Task Over_budget_is_a_warning()
    {
        _resources.Read().Returns(
            Snapshot(cpu: TimeSpan.Zero, privateMb: 80),
            Snapshot(cpu: TimeSpan.FromSeconds(30), privateMb: 130)); // 5 % CPU, 130 MB
        await using var sut = new ResourceMonitor(_resources, _clock, logger: _log, processorCount: 1);

        sut.Start();
        _clock.Advance(TimeSpan.FromMinutes(10));

        _log.Entries[^1].Level.Should().Be(LogLevel.Warning);
        _log.Lines[^1].Should().Contain("OVER BUDGET (cpu+memory");
    }

    [Fact]
    public async Task Never_polls_faster_than_every_10s()
    {
        _resources.Read().Returns(Snapshot(TimeSpan.Zero, 80));
        await using var sut = new ResourceMonitor(_resources, _clock, new ResourceMonitorOptions { Interval = TimeSpan.FromMilliseconds(100) });

        sut.Interval.Should().Be(TimeSpan.FromSeconds(10));
        new ResourceMonitorOptions().Interval.Should().Be(TimeSpan.FromMinutes(10));
        new ResourceMonitorOptions().CpuBudgetPercent.Should().Be(0.5);
        new ResourceMonitorOptions().PrivateBytesBudget.Should().Be(120L * 1024 * 1024);
    }

    [Fact]
    public async Task A_failing_reader_never_throws()
    {
        _resources.Read().Returns(_ => throw new InvalidOperationException("boom"));
        await using var sut = new ResourceMonitor(_resources, _clock, logger: _log);

        sut.Start();
        _clock.Advance(TimeSpan.FromMinutes(10));

        sut.LatestLine.Should().Be("RESOURCES unavailable");
    }

    private static ProcessResourceSnapshot Snapshot(TimeSpan cpu, int privateMb) =>
        new(cpu, privateMb * 1024L * 1024, 700, 40, 30, 25);

    private sealed class ListLogger : ILogger<ResourceMonitor>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IReadOnlyList<string> Lines => [.. Entries.Select(e => e.Text)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

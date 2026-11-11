using DualAudio.Core.Connection;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.Core.Tests.Connection;

public sealed class ReconnectPolicyTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Delays_double_from_2s_and_cap_at_30s()
    {
        using var policy = new ReconnectPolicy(_clock);

        Enumerable.Range(1, 7).Select(policy.GetDelay).Select(d => d.TotalSeconds)
            .Should().Equal(2, 4, 8, 16, 30, 30, 30);
    }

    [Fact]
    public void Very_high_attempt_numbers_stay_capped()
    {
        using var policy = new ReconnectPolicy(_clock);

        policy.GetDelay(1000).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Attempt_runs_only_after_its_delay_on_the_injected_clock()
    {
        using var policy = new ReconnectPolicy(_clock);
        var runs = 0;

        policy.TrySchedule(() => { runs++; return Task.CompletedTask; }).Should().BeTrue();
        policy.IsPending.Should().BeTrue();
        policy.NextAttemptAt.Should().Be(_clock.UtcNow + TimeSpan.FromSeconds(2));

        _clock.Advance(TimeSpan.FromSeconds(1.99));
        runs.Should().Be(0);

        _clock.Advance(TimeSpan.FromSeconds(0.01));
        runs.Should().Be(1);
        policy.IsPending.Should().BeFalse();
        policy.NextAttemptAt.Should().BeNull();
    }

    [Fact]
    public void Successive_schedules_use_growing_delays()
    {
        using var policy = new ReconnectPolicy(_clock);

        for (var i = 0; i < 6; i++)
        {
            policy.TrySchedule(() => Task.CompletedTask);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _clock.RequestedDelays.Select(d => d.TotalSeconds).Should().Equal(2, 4, 8, 16, 30, 30);
        policy.AttemptsMade.Should().Be(6);
    }

    [Fact]
    public void Stops_after_max_attempts()
    {
        using var policy = new ReconnectPolicy(_clock, new ReconnectOptions { MaxAttempts = 2 });

        policy.TrySchedule(() => Task.CompletedTask).Should().BeTrue();
        policy.TrySchedule(() => Task.CompletedTask).Should().BeTrue();
        policy.IsExhausted.Should().BeTrue();
        policy.TrySchedule(() => Task.CompletedTask).Should().BeFalse();
    }

    [Fact]
    public void Cancel_prevents_the_pending_attempt_and_resets_the_count()
    {
        using var policy = new ReconnectPolicy(_clock);
        var runs = 0;
        policy.TrySchedule(() => { runs++; return Task.CompletedTask; });

        policy.Cancel();
        _clock.Advance(TimeSpan.FromMinutes(5));

        runs.Should().Be(0);
        policy.IsPending.Should().BeFalse();
        policy.AttemptsMade.Should().Be(0);
    }

    [Fact]
    public void Scheduling_again_replaces_the_pending_attempt()
    {
        using var policy = new ReconnectPolicy(_clock);
        var first = 0;
        var second = 0;

        policy.TrySchedule(() => { first++; return Task.CompletedTask; });
        policy.TrySchedule(() => { second++; return Task.CompletedTask; });
        _clock.Advance(TimeSpan.FromMinutes(1));

        first.Should().Be(0);
        second.Should().Be(1);
    }

    [Fact]
    public void A_throwing_attempt_does_not_escape()
    {
        using var policy = new ReconnectPolicy(_clock);
        policy.TrySchedule(() => throw new InvalidOperationException("boom"));

        var act = () => _clock.Advance(TimeSpan.FromSeconds(2));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 30, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 30, 0)]
    public void Invalid_options_are_rejected(int initialSeconds, int maxSeconds, int maxAttempts)
    {
        var options = new ReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(initialSeconds),
            MaxDelay = TimeSpan.FromSeconds(maxSeconds),
            MaxAttempts = maxAttempts,
        };

        var act = () => new ReconnectPolicy(_clock, options);

        act.Should().Throw<ArgumentException>();
    }
}

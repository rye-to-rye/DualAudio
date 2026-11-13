using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.Core.Tests.Audio;

public sealed class PhoneAudioServiceTests : IAsyncDisposable
{
    private const string Headset = "{0.0.0.00000000}.{headset}";
    private const string Speakers = "{0.0.0.00000000}.{speakers}";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly CoordinatorHarness _h = new();
    private readonly IAudioSessionController _controller = Substitute.For<IAudioSessionController>();
    private readonly InMemorySettingsStore _store;
    private readonly PhoneAudioService _sut;
    private readonly List<PhoneAudioChangedEventArgs> _events = [];
    private PhoneAudioStatus _status = PhoneAudioStatus.Unavailable;

    public PhoneAudioServiceTests()
    {
        _store = new InMemorySettingsStore(AppSettings.Default with { PhoneVolume = 30, PhoneMuted = true });
        _controller.GetPhoneAudioStatusAsync(default).ReturnsForAnyArgs(_ => Task.FromResult(_status));
        _controller.SetPhoneVolumeAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        _controller.SetPhoneMutedAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        _sut = new PhoneAudioService(_controller, _h.Sut, _store, _h.Clock);
        _sut.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    private int StatusReads => _controller.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAudioSessionController.GetPhoneAudioStatusAsync));

    private List<bool> ReceivingTransitions
    {
        get
        {
            lock (_events)
            {
                return [.. _events.Where(e => e.Previous.IsReceivingAudio != e.Current.IsReceivingAudio).Select(e => e.Current.IsReceivingAudio)];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _h.DisposeAsync();
    }

    // ------------------------------------------------------------------ volume / mute + settings

    [Fact]
    public async Task Initialize_loads_saved_volume_and_mute_and_applies_them()
    {
        await _sut.InitializeAsync();
        await SettleAsync();

        _sut.VolumePercent.Should().Be(30);
        _sut.Muted.Should().BeTrue();
        await _controller.Received(1).SetPhoneVolumeAsync(30, Arg.Any<CancellationToken>());
        await _controller.Received(1).SetPhoneMutedAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_applied_before_initialize_even_when_connected()
    {
        await _h.ConnectFullyAsync();
        await SettleAsync();

        await _controller.DidNotReceiveWithAnyArgs().SetPhoneVolumeAsync(default, default);
        await _controller.DidNotReceiveWithAnyArgs().SetPhoneMutedAsync(default, default);
    }

    [Fact]
    public async Task SetVolume_clamps_applies_and_persists_without_touching_other_settings()
    {
        await _sut.InitializeAsync();

        await _sut.SetVolumeAsync(150);
        await SettleAsync();

        _sut.VolumePercent.Should().Be(100);
        await _controller.Received(1).SetPhoneVolumeAsync(100, Arg.Any<CancellationToken>());
        _store.Current.PhoneVolume.Should().Be(100);
        _store.Current.PhoneMuted.Should().BeTrue("mute was not changed");
    }

    [Fact]
    public async Task SetMuted_applies_and_persists()
    {
        await _sut.InitializeAsync();

        await _sut.SetMutedAsync(false);
        await SettleAsync();

        _sut.Muted.Should().BeFalse();
        await _controller.Received(1).SetPhoneMutedAsync(false, Arg.Any<CancellationToken>());
        _store.Current.PhoneMuted.Should().BeFalse();
        _store.Current.PhoneVolume.Should().Be(30);
    }

    [Fact]
    public async Task Saved_volume_is_reapplied_on_every_Connected()
    {
        await _sut.InitializeAsync();
        await SettleAsync();
        _controller.ClearReceivedCalls();

        await _h.ConnectFullyAsync();
        await SettleAsync();
        await _h.Sut.DisconnectAsync();
        await _h.ConnectFullyAsync();
        await SettleAsync();

        await _controller.Received(2).SetPhoneVolumeAsync(30, Arg.Any<CancellationToken>());
        await _controller.Received(2).SetPhoneMutedAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Controller_failures_never_reach_the_caller()
    {
        _controller.SetPhoneVolumeAsync(default, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("COM")));
        _controller.GetPhoneAudioStatusAsync(default).ReturnsForAnyArgs(Task.FromException<PhoneAudioStatus>(new InvalidOperationException("COM")));
        await _sut.InitializeAsync();

        var set = () => _sut.SetVolumeAsync(50);

        await set.Should().NotThrowAsync();
        _sut.VolumePercent.Should().Be(50);
        _sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailable_follows_whether_any_phone_session_exists()
    {
        _status = Status(active: false, peak: 0, onDefault: false);
        await _sut.InitializeAsync();
        await SettleAsync();
        _sut.IsAvailable.Should().BeTrue();

        _status = PhoneAudioStatus.Unavailable;
        _controller.PhoneSessionsChanged += Raise.Event();
        await SettleAsync();
        _sut.IsAvailable.Should().BeFalse();
    }

    // ------------------------------------------------------------------ IsReceivingAudio

    [Fact]
    public async Task Does_not_poll_while_not_connected()
    {
        await _sut.InitializeAsync();
        await SettleAsync();
        var reads = StatusReads;

        await AdvanceAsync(TimeSpan.FromSeconds(5));

        StatusReads.Should().Be(reads);
        _sut.IsReceivingAudio.Should().BeFalse();
    }

    [Fact]
    public async Task Polls_every_250ms_while_connected()
    {
        await _h.ConnectFullyAsync();
        await SettleAsync();
        var reads = StatusReads;

        await AdvanceAsync(Poll);
        await AdvanceAsync(Poll);
        await AdvanceAsync(Poll);

        StatusReads.Should().Be(reads + 3);
    }

    [Fact]
    public void Poll_interval_is_never_faster_than_250ms()
    {
        var sut = new PhoneAudioService(_controller, _h.Sut, _store, _h.Clock, new PhoneAudioOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        sut.PollInterval.Should().Be(Poll);
    }

    [Fact]
    public async Task Receiving_is_true_when_active_on_default_with_peak()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        _sut.IsReceivingAudio.Should().BeTrue();
        ReceivingTransitions.Should().Equal(true);
    }

    [Fact]
    public async Task Pause_turns_receiving_false_after_10s_of_silence_and_resume_turns_it_back_on()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        _sut.IsReceivingAudio.Should().BeTrue();

        // Paused: session stays Active (link Opened) but peak is 0.
        _status = Status(active: true, peak: 0f);
        await AdvanceAsync(TimeSpan.FromMilliseconds(9750));
        _sut.IsReceivingAudio.Should().BeTrue("the last sound was less than 10 s ago");

        await AdvanceAsync(Poll);
        _sut.IsReceivingAudio.Should().BeFalse("no sound for 10 s");

        _status = Status(active: true, peak: 0.05f);
        await AdvanceAsync(Poll);
        _sut.IsReceivingAudio.Should().BeTrue();

        ReceivingTransitions.Should().Equal(true, false, true);
    }

    [Fact]
    public async Task Silence_of_9s_stays_true_and_11s_goes_false()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        _status = Status(active: true, peak: 0f);

        await AdvanceAsync(TimeSpan.FromSeconds(9));
        _sut.IsReceivingAudio.Should().BeTrue("9 s of silence is within the 10 s hold (e.g. between TikTok videos)");

        await AdvanceAsync(TimeSpan.FromSeconds(2));
        _sut.IsReceivingAudio.Should().BeFalse("11 s of silence exceeds the hold");
        ReceivingTransitions.Should().Equal(true, false);
    }

    [Fact]
    public async Task Gap_between_videos_shorter_than_hold_does_not_flicker()
    {
        _status = Status(active: true, peak: 0.1f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        for (var gap = 0; gap < 3; gap++)
        {
            _status = Status(active: true, peak: 0f);
            await AdvanceAsync(TimeSpan.FromSeconds(8));
            _status = Status(active: true, peak: 0.1f);
            await AdvanceAsync(Poll);
        }

        ReceivingTransitions.Should().Equal(true);
    }

    [Fact]
    public async Task Mute_while_receiving_keeps_receiving_true()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        _sut.IsReceivingAudio.Should().BeTrue();

        // Hardware: muting makes Windows report the session Inactive while the peak stays > 0.
        await _sut.SetMutedAsync(true);
        _status = Status(active: false, peak: 0.127f);
        _controller.PhoneSessionsChanged += Raise.Event();
        await SettleAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(15));

        _sut.IsReceivingAudio.Should().BeTrue("the mute itself must not flip it to false");
        _sut.Muted.Should().BeTrue();
        ReceivingTransitions.Should().Equal(true);
    }

    [Fact]
    public async Task Mute_while_not_receiving_keeps_receiving_false()
    {
        _status = Status(active: true, peak: 0f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        await _sut.SetMutedAsync(true);
        await AdvanceAsync(TimeSpan.FromSeconds(2));

        _sut.IsReceivingAudio.Should().BeFalse();
        ReceivingTransitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Unmute_after_long_mute_starts_a_fresh_hold()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        await _sut.SetMutedAsync(true);
        _status = Status(active: false, peak: 0.127f);
        await AdvanceAsync(TimeSpan.FromSeconds(30));

        // Unmuted but phone now paused: stays true for the hold, then false.
        _status = Status(active: true, peak: 0f);
        await _sut.SetMutedAsync(false);
        await AdvanceAsync(TimeSpan.FromSeconds(9));
        _sut.IsReceivingAudio.Should().BeTrue("unmuting must not drop it at once");

        await AdvanceAsync(TimeSpan.FromSeconds(2));
        _sut.IsReceivingAudio.Should().BeFalse();
        ReceivingTransitions.Should().Equal(true, false);
    }

    [Fact]
    public async Task Quiet_moments_below_hold_do_not_flicker()
    {
        _status = Status(active: true, peak: 0.1f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        for (var i = 0; i < 10; i++)
        {
            _status = Status(active: true, peak: i % 4 == 0 ? 0.1f : 0f);
            await AdvanceAsync(Poll);
        }

        ReceivingTransitions.Should().Equal(true);
    }

    [Fact]
    public async Task Peak_at_or_below_threshold_is_not_sound()
    {
        _status = Status(active: true, peak: 0.001f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        await AdvanceAsync(Poll);

        _sut.IsReceivingAudio.Should().BeFalse();
    }

    [Fact]
    public async Task Inactive_unmuted_session_turns_receiving_false_only_after_the_hold()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        _status = Status(active: false, peak: 0f);
        _controller.PhoneSessionsChanged += Raise.Event();
        await SettleAsync();
        _sut.IsReceivingAudio.Should().BeTrue("hysteresis: false only after 10 s without a peak");

        await AdvanceAsync(TimeSpan.FromSeconds(10));
        _sut.IsReceivingAudio.Should().BeFalse();
    }

    [Fact]
    public async Task Peak_on_an_inactive_session_does_not_turn_receiving_on()
    {
        _status = Status(active: false, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        await AdvanceAsync(Poll);

        _sut.IsReceivingAudio.Should().BeFalse();
    }

    [Fact]
    public async Task Peak_on_a_non_default_session_does_not_count()
    {
        _status = Status(active: true, peak: 0f, onDefault: false) with { Peak = 0f };
        await _h.ConnectFullyAsync();
        await SettleAsync();
        await AdvanceAsync(Poll);

        _sut.IsReceivingAudio.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_turns_receiving_false_and_stops_polling()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        _sut.IsReceivingAudio.Should().BeTrue();

        await _h.Sut.DisconnectAsync();
        await SettleAsync();
        var reads = StatusReads;
        await AdvanceAsync(TimeSpan.FromSeconds(2));

        _sut.IsReceivingAudio.Should().BeFalse();
        _sut.Current.IsAvailable.Should().BeTrue("Connected and IsReceivingAudio are separate from session availability");
        StatusReads.Should().Be(reads);
        ReceivingTransitions.Should().Equal(true, false);
    }

    [Fact]
    public async Task Changed_handlers_may_call_back_into_the_service()
    {
        var calledBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Changed += (_, e) =>
        {
            if (e.Current.IsReceivingAudio && !e.Previous.IsReceivingAudio)
            {
                // Blocking call from inside the handler: must not deadlock (raised outside the lock).
                _sut.SetVolumeAsync(10).Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                calledBack.TrySetResult();
            }
        };
        _status = Status(active: true, peak: 0.127f);

        await _h.ConnectFullyAsync();
        await calledBack.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _sut.VolumePercent.Should().Be(10);
    }

    // ------------------------------------------------------------------ Level (phone level meter)

    [Fact]
    public async Task Level_follows_the_peak_while_connected()
    {
        var levels = RecordLevels();
        _status = Status(active: true, peak: 0.3f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        _sut.Level.Should().Be(0.3f);

        _status = Status(active: true, peak: 0.6f);
        await AdvanceAsync(Poll);

        _sut.Level.Should().Be(0.6f);
        levels.Select(l => l.Level).Should().Equal(0.3f, 0.6f);
    }

    [Fact]
    public async Task Level_is_0_and_never_raised_while_not_connected()
    {
        var levels = RecordLevels();
        _status = Status(active: true, peak: 0.5f);
        await _sut.InitializeAsync();
        _controller.PhoneSessionsChanged += Raise.Event();
        await AdvanceAsync(TimeSpan.FromSeconds(2));

        _sut.Level.Should().Be(0f);
        levels.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnect_drops_the_level_to_0_at_once_and_stops_level_events()
    {
        var levels = RecordLevels();
        _status = Status(active: true, peak: 0.4f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        await _h.Sut.DisconnectAsync();
        await SettleAsync(); // no clock advance: the drop to 0 is not throttled

        _sut.Level.Should().Be(0f);
        levels.Select(l => l.Level).Should().Equal(0.4f, 0f);

        await AdvanceAsync(TimeSpan.FromSeconds(2));
        levels.Should().HaveCount(2, "nothing is raised while not Connected");
    }

    [Fact]
    public async Task Level_events_are_throttled_to_at_most_10_per_second()
    {
        var levels = RecordLevels();
        _status = Status(active: true, peak: 0.1f);
        await _h.ConnectFullyAsync();
        await SettleAsync();
        var readsBefore = StatusReads;

        // Every 50 ms a session event with a new peak (faster than the meter may update), for one second.
        for (var i = 0; i < 20; i++)
        {
            _status = Status(active: true, peak: 0.2f + (i * 0.01f));
            _controller.PhoneSessionsChanged += Raise.Event();
            await SettleAsync();
            _h.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await SettleAsync();
        }

        var times = levels.Select(l => l.At).ToList();
        times.Should().HaveCountLessThanOrEqualTo(11, "~10 per second (+1 for the value at connect)");
        times.Zip(times.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap >= PhoneAudioOptions.MinLevelInterval);
        (StatusReads - readsBefore).Should().Be(20 + 4, "the level adds no reads: only the session events and the 250 ms poll");
    }

    [Fact]
    public async Task Level_keeps_showing_while_muted()
    {
        _status = Status(active: true, peak: 0.127f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        // Hardware: muting makes Windows report the session Inactive while the peak stays > 0.
        await _sut.SetMutedAsync(true);
        _status = Status(active: false, peak: 0.127f);
        await AdvanceAsync(Poll);

        _sut.Level.Should().Be(0.127f);
    }

    [Fact]
    public async Task Level_is_clamped_to_0_1()
    {
        _status = Status(active: true, peak: 1.7f);
        await _h.ConnectFullyAsync();
        await SettleAsync();

        _sut.Level.Should().Be(1f);
    }

    // ------------------------------------------------------------------ helpers

    private List<(float Level, DateTimeOffset At)> RecordLevels()
    {
        var levels = new List<(float, DateTimeOffset)>();
        _sut.LevelChanged += (_, e) =>
        {
            lock (levels)
            {
                levels.Add((e.Level, _h.Clock.UtcNow));
            }
        };
        return levels;
    }

    private static PhoneAudioStatus Status(bool active, float peak, bool onDefault = true) =>
        new(
            [
                new PhoneSessionState(Headset, onDefault, onDefault && active, 30, false),
                new PhoneSessionState(Speakers, !onDefault, !onDefault && active, 30, false),
            ],
            onDefault ? peak : 0f);

    private async Task SettleAsync()
    {
        await _h.SettleAsync();
        await _sut.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        // Step in poll-sized increments so every poll tick runs, like real time.
        var remaining = by;
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining < Poll ? remaining : Poll;
            await SettleAsync();
            _h.Clock.Advance(step);
            await SettleAsync();
            remaining -= step;
        }
    }
}

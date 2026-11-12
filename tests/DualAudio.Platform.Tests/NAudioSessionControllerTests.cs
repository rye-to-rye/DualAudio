using DualAudio.Platform.Tests.Fakes;
using DualAudio.Platform.Windows.Audio;
using static DualAudio.Platform.Tests.Fakes.FakeCoreAudioBackend;

namespace DualAudio.Platform.Tests;

/// <summary>Controller logic against an in-memory backend (no Core Audio, no hardware).</summary>
public sealed class NAudioSessionControllerTests : IDisposable
{
    private const string Headset = "{0.0.0.00000000}.{headset}";
    private const string Speakers = "{0.0.0.00000000}.{speakers}";
    private const string Usb = "{0.0.0.00000000}.{usb}";

    private readonly FakeCoreAudioBackend _backend = new();
    private readonly NAudioSessionController _sut;
    private readonly FakeSession _phoneOnHeadset;
    private readonly FakeSession _phoneOnSpeakers;
    private readonly FakeSession _chromeOnHeadset;
    private int _sessionsChanged;

    public NAudioSessionControllerTests(ITestOutputHelper output)
    {
        // Phase 0 layout: one phone session per render endpoint, Active only on the default.
        _backend.AddEndpoint(Headset, isDefault: true);
        _backend.AddEndpoint(Speakers);
        _phoneOnHeadset = _backend.AddSession(Headset, FakeSession.Phone());
        _phoneOnHeadset.Active = true;
        _phoneOnSpeakers = _backend.AddSession(Speakers, FakeSession.Phone());
        _chromeOnHeadset = _backend.AddSession(Headset, FakeSession.App());
        _chromeOnHeadset.Active = true;

        _sut = new NAudioSessionController(_backend, new TestOutputLogger<NAudioSessionController>(output));
        _sut.PhoneSessionsChanged += (_, _) => Interlocked.Increment(ref _sessionsChanged);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Nothing_touches_Core_Audio_before_the_first_call()
    {
        await _sut.FlushAsync();
        _backend.Started.Should().BeFalse();

        await _sut.GetPhoneSessionsAsync(CancellationToken.None);
        _backend.Started.Should().BeTrue();
    }

    [Fact]
    public async Task Finds_one_phone_session_per_endpoint_and_ignores_other_sessions()
    {
        var sessions = await _sut.GetPhoneSessionsAsync(CancellationToken.None);

        sessions.Select(s => s.EndpointId).Should().BeEquivalentTo([Headset, Speakers]);
        sessions.Should().OnlyContain(s => s.DisplayName!.Contains("A2DP SNK"));
    }

    [Fact]
    public async Task SetPhoneVolume_applies_to_every_phone_session_on_every_endpoint_and_never_to_others()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);

        _phoneOnHeadset.Volume.Should().BeApproximately(0.30f, 0.0001f);
        _phoneOnSpeakers.Volume.Should().BeApproximately(0.30f, 0.0001f);
        _chromeOnHeadset.Volume.Should().Be(1f);
        _chromeOnHeadset.Writes.Should().Be(0);
    }

    [Fact]
    public async Task SetPhoneMuted_applies_to_every_phone_session_and_never_to_others()
    {
        await _sut.SetPhoneMutedAsync(true, CancellationToken.None);

        _phoneOnHeadset.Mute.Should().BeTrue();
        _phoneOnSpeakers.Mute.Should().BeTrue();
        _chromeOnHeadset.Mute.Should().BeFalse();
        _chromeOnHeadset.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(-5, 0f)]
    [InlineData(250, 1f)]
    public async Task Volume_is_clamped(int requested, float expected)
    {
        await _sut.SetPhoneVolumeAsync(requested, CancellationToken.None);

        _phoneOnHeadset.Volume.Should().Be(expected);
    }

    [Fact]
    public async Task Only_writes_when_the_value_differs()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        var writes = _phoneOnHeadset.Writes;

        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);

        _phoneOnHeadset.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task Reapplies_when_a_new_phone_session_is_created()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        await _sut.SetPhoneMutedAsync(true, CancellationToken.None);

        // e.g. after a reconnect Windows creates a fresh session at 100 %.
        _backend.RemoveSession(Headset, _phoneOnHeadset);
        var fresh = _backend.AddSession(Headset, FakeSession.Phone());
        _backend.RaiseSessionCreated(Headset);
        await _sut.FlushAsync();

        fresh.Volume.Should().BeApproximately(0.30f, 0.0001f);
        fresh.Mute.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_non_phone_session_is_left_alone()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);

        var game = _backend.AddSession(Headset, FakeSession.App("game"));
        _backend.RaiseSessionCreated(Headset);
        await _sut.FlushAsync();

        game.Volume.Should().Be(1f);
        game.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Reapplies_when_the_default_endpoint_changes()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        await _sut.SetPhoneMutedAsync(true, CancellationToken.None);

        // The session on the new default was reset meanwhile (e.g. by Windows or the volume mixer).
        _phoneOnSpeakers.Volume = 1f;
        _phoneOnSpeakers.Mute = false;
        _backend.SetDefault(Speakers);
        _phoneOnHeadset.Active = false;
        _phoneOnSpeakers.Active = true;
        _backend.RaiseDefaultChanged();
        await _sut.FlushAsync();

        _phoneOnSpeakers.Volume.Should().BeApproximately(0.30f, 0.0001f);
        _phoneOnSpeakers.Mute.Should().BeTrue();
        _chromeOnHeadset.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Reapplies_when_an_endpoint_is_added()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);

        _backend.AddEndpoint(Usb);
        var onUsb = _backend.AddSession(Usb, FakeSession.Phone());
        _backend.RaiseEndpointsChanged();
        await _sut.FlushAsync();

        onUsb.Volume.Should().BeApproximately(0.30f, 0.0001f);
    }

    [Fact]
    public async Task Without_phone_sessions_set_is_a_noop_reports_unavailable_and_applies_later()
    {
        _backend.RemoveSession(Headset, _phoneOnHeadset);
        _backend.RemoveSession(Speakers, _phoneOnSpeakers);

        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        var status = await _sut.GetPhoneAudioStatusAsync(CancellationToken.None);

        status.Available.Should().BeFalse();
        _chromeOnHeadset.Writes.Should().Be(0);

        var phone = _backend.AddSession(Headset, FakeSession.Phone());
        _backend.RaiseSessionCreated(Headset);
        await _sut.FlushAsync();
        phone.Volume.Should().BeApproximately(0.30f, 0.0001f);
    }

    [Fact]
    public async Task Status_reports_every_session_and_the_default_sessions_peak_only()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        _phoneOnHeadset.Peak = 0.127f;
        _phoneOnSpeakers.Peak = 0.9f; // not the default endpoint: ignored
        _chromeOnHeadset.Peak = 0.5f; // not a phone session: ignored

        var status = await _sut.GetPhoneAudioStatusAsync(CancellationToken.None);

        status.Available.Should().BeTrue();
        status.IsActiveOnDefault.Should().BeTrue();
        status.Peak.Should().BeApproximately(0.127f, 0.0001f);
        status.Sessions.Should().HaveCount(2).And.OnlyContain(s => s.VolumePercent == 30 && !s.Muted);
        status.Sessions.Single(s => s.IsOnDefaultEndpoint).EndpointId.Should().Be(Headset);
        (await _sut.IsReceivingAudioAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task PhoneSessionsChanged_fires_when_the_default_endpoint_session_goes_inactive_or_disappears()
    {
        await _sut.GetPhoneSessionsAsync(CancellationToken.None);
        await WaitForEventsAsync();
        var baseline = Volatile.Read(ref _sessionsChanged);

        _phoneOnHeadset.Active = false;
        _phoneOnHeadset.RaiseStateChanged();
        await _sut.FlushAsync();
        await WaitForEventsAsync(baseline + 1);

        _backend.RemoveSession(Headset, _phoneOnHeadset);
        _phoneOnHeadset.Expired = true;
        _phoneOnHeadset.RaiseStateChanged();
        await _sut.FlushAsync();
        await WaitForEventsAsync(baseline + 2);

        (await _sut.GetPhoneAudioStatusAsync(CancellationToken.None)).Sessions.Should().ContainSingle(s => s.EndpointId == Speakers);
    }

    [Fact]
    public async Task PhoneSessionsChanged_does_not_fire_for_non_default_endpoint_state_changes()
    {
        await _sut.GetPhoneSessionsAsync(CancellationToken.None);
        await WaitForEventsAsync();
        var baseline = Volatile.Read(ref _sessionsChanged);

        _phoneOnSpeakers.Active = true;
        _phoneOnSpeakers.RaiseStateChanged();
        await _sut.FlushAsync();
        await Task.Delay(100);

        Volatile.Read(ref _sessionsChanged).Should().Be(baseline);
    }

    [Fact]
    public async Task Dispose_releases_every_handle_and_the_backend()
    {
        await _sut.SetPhoneVolumeAsync(30, CancellationToken.None);
        _backend.LiveHandles.Should().Be(2, "only the two phone sessions are kept");

        _sut.Dispose();

        _backend.LiveHandles.Should().Be(0);
        _backend.Disposed.Should().BeTrue();
        (await _sut.GetPhoneAudioStatusAsync(CancellationToken.None)).Available.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelled_call_throws_OperationCanceled()
    {
        var call = () => _sut.SetPhoneVolumeAsync(30, new CancellationToken(canceled: true));

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task WaitForEventsAsync(int atLeast = 0)
    {
        // Events are raised off the worker thread.
        for (var i = 0; i < 100 && Volatile.Read(ref _sessionsChanged) < atLeast; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(50);
        Volatile.Read(ref _sessionsChanged).Should().BeGreaterThanOrEqualTo(atLeast);
    }
}

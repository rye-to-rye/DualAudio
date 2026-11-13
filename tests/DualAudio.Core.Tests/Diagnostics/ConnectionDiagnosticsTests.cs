using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Tests.Fakes;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.Core.Tests.Diagnostics;

public sealed class ConnectionDiagnosticsTests
{
    private static readonly IReadOnlyList<AudioEndpoint> Endpoints =
    [
        new(HeadsetEndpointId, "Headset Earphone (Cirrus Logic)", true),
        new(SpeakersEndpointId, "Speakers (Cirrus Logic)", false),
    ];

    [Fact]
    public void Line_has_every_field_on_one_line_and_no_address_or_phone_name()
    {
        var status = new PhoneAudioStatus(
            [
                new PhoneSessionState(HeadsetEndpointId, true, true, 100, false),
                new PhoneSessionState(SpeakersEndpointId, false, false, 100, false),
            ],
            0.127f);

        var line = ConnectionDiagnostics.Format(
            "opened+3s",
            ConnectionState.Connected,
            ReceiverState.Opened,
            status,
            Endpoints,
            priming: false,
            staleBeforeOpen: true,
            automaticReopenUsed: false,
            sinceClosed: TimeSpan.FromSeconds(12.34),
            deviceId: PhoneId);

        line.Should().StartWith("STATE opened+3s:")
            .And.Contain("coord=Connected")
            .And.Contain("recv=Opened")
            .And.Contain("session=found")
            .And.Contain("sessionEndpoint=\"Headset Earphone (Cirrus Logic)\"")
            .And.Contain("sessionState=Active")
            .And.Contain("peak=0.127")
            .And.Contain("sessions=2")
            .And.Contain("default=\"Headset Earphone (Cirrus Logic)\"")
            .And.Contain("muted=no")
            .And.Contain("priming=no")
            .And.Contain("staleBeforeOpen=yes")
            .And.Contain("autoReopenUsed=no")
            .And.Contain("sinceClosed=12.3s")
            .And.Contain("device=…BBCC");
        line.Should().NotContain("\n").And.NotContain("5c:f3", "no full Bluetooth address").And.NotContain(PhoneName);
    }

    [Fact]
    public void Line_without_a_session_or_any_close_says_so()
    {
        var line = ConnectionDiagnostics.Format(
            "connect", ConnectionState.Enabling, ReceiverState.Closed, PhoneAudioStatus.Unavailable, Endpoints,
            priming: false, staleBeforeOpen: false, automaticReopenUsed: false, sinceClosed: null, deviceId: null);

        line.Should().Contain("session=none").And.Contain("sessionState=-").And.Contain("sinceClosed=never").And.Contain("device=<none>");
    }

    [Fact]
    public void Failed_reads_show_as_question_marks()
    {
        var line = ConnectionDiagnostics.Format(
            "session-changed", ConnectionState.Connected, null, null, null,
            priming: true, staleBeforeOpen: false, automaticReopenUsed: true, sinceClosed: null, deviceId: PhoneId);

        line.Should().Contain("recv=?").And.Contain("session=?").And.Contain("peak=?").And.Contain("default=\"?\"")
            .And.Contain("priming=yes").And.Contain("autoReopenUsed=yes");
    }

    [Fact]
    public async Task Writes_a_line_at_connect_opened_3s_10s_and_on_every_session_change()
    {
        await using var h = new CoordinatorHarness();
        var outputs = Substitute.For<IOutputDeviceService>();
        outputs.GetRenderEndpointsAsync(default).ReturnsForAnyArgs(Task.FromResult(Endpoints));
        await using var diagnostics = new ConnectionDiagnostics(h.Sut, h.Sessions, outputs, h.Clock);
        h.SetPhoneSession(active: false);

        await h.ConnectFullyAsync();
        await diagnostics.WhenIdleAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(3));
        await diagnostics.WhenIdleAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(7));
        await diagnostics.WhenIdleAsync();
        h.SetPhoneSession(active: true, peak: 0.2f);
        h.Sessions.PhoneSessionsChanged += Raise.Event();
        await diagnostics.WhenIdleAsync();

        var triggers = diagnostics.RecentLines.Select(l => l.Split(' ')[3].TrimEnd(':')).ToList();
        triggers.Should().ContainInOrder("connect", "opened", "opened+3s", "opened+10s", "session-changed");
        diagnostics.RecentLines[^1].Should().Contain("sessionState=Active").And.Contain("peak=0.200").And.Contain("coord=Connected");
    }

    [Fact]
    public async Task Keeps_only_the_most_recent_lines()
    {
        await using var h = new CoordinatorHarness();
        var outputs = Substitute.For<IOutputDeviceService>();
        await using var diagnostics = new ConnectionDiagnostics(
            h.Sut, h.Sessions, outputs, h.Clock, new ConnectionDiagnosticsOptions { MaxRecentLines = 5 });

        for (var i = 0; i < 12; i++)
        {
            h.Sessions.PhoneSessionsChanged += Raise.Event();
            await diagnostics.WhenIdleAsync();
        }

        diagnostics.RecentLines.Should().HaveCount(5);
        new ConnectionDiagnosticsOptions().MaxRecentLines.Should().Be(50);
    }
}

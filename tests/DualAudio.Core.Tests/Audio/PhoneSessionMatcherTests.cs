using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;

namespace DualAudio.Core.Tests.Audio;

/// <summary>Cases taken from docs/phase0-findings.md (session dumps of 2026-11-11).</summary>
public sealed class PhoneSessionMatcherTests
{
    private const string Guid = "{2EA30F40-6E9F-4709-9327-A4E880941760}";
    private const string ZeroGuid = "{00000000-0000-0000-0000-000000000000}";
    private const string HeadsetEndpoint = "{0.0.0.00000000}.{b2498d41-0000-0000-0000-000000000001}";
    private const string SpeakersEndpoint = "{0.0.0.00000000}.{b2498d41-0000-0000-0000-000000000002}";
    private const string FifineEndpoint = "{0.0.0.00000000}.{b2498d41-0000-0000-0000-000000000003}";
    private const string SvchostPath = @"\Device\HarddiskVolume3\Windows\System32\svchost.exe";

    public static TheoryData<string, PhoneSessionInfo> PhoneSessions => new()
    {
        { "Phase 0 session [6] on the headset", Phone(HeadsetEndpoint) },
        { "same session on Speakers (Cirrus Logic)", Phone(SpeakersEndpoint) },
        { "Inactive copy on Speakers (fifine Microphone)", Phone(FifineEndpoint) },
        { "process reported as svchost.exe", Phone(HeadsetEndpoint) with { ProcessName = "svchost.exe" } },
        { "process reported as full path", Phone(HeadsetEndpoint) with { ProcessName = @"C:\Windows\System32\SVCHOST.EXE" } },
        { "different PID after reboot", Phone(HeadsetEndpoint) with { ProcessId = 1188 } },
        { "localized 'Microphone'", Phone(HeadsetEndpoint) with { DisplayName = "Mikrofon (Shlawg A2DP SNK)" } },
        { "another phone name", Phone(HeadsetEndpoint) with { DisplayName = "Microphone (Pixel 9 A2DP SNK)" } },
    };

    public static TheoryData<string, PhoneSessionInfo> NonPhoneSessions => new()
    {
        {
            "Chrome (YouTube)",
            new(SpeakersEndpoint, "chrome", 14512, string.Empty,
                $@"{SpeakersEndpoint}|\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe%b{ZeroGuid}",
                $@"{SpeakersEndpoint}|\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe%b{ZeroGuid}|8%b14512",
                false)
        },
        {
            "Discord",
            new(HeadsetEndpoint, "Discord", 9920, "Discord",
                $@"{HeadsetEndpoint}|\Device\HarddiskVolume3\Users\u\AppData\Local\Discord\app-1.0.9\Discord.exe%b{ZeroGuid}",
                $@"{HeadsetEndpoint}|\Device\HarddiskVolume3\Users\u\AppData\Local\Discord\app-1.0.9\Discord.exe%b{ZeroGuid}|8%b9920",
                false)
        },
        {
            "Steam",
            new(HeadsetEndpoint, "steamwebhelper", 7720, string.Empty,
                $@"{HeadsetEndpoint}|\Device\HarddiskVolume3\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe%b{ZeroGuid}",
                $@"{HeadsetEndpoint}|\Device\HarddiskVolume3\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe%b{ZeroGuid}|8%b7720",
                false)
        },
        {
            "System sounds (non-zero GUID of its own)",
            new(HeadsetEndpoint, "Idle", 0, @"@%SystemRoot%\System32\AudioSrv.Dll,-202",
                $"{HeadsetEndpoint}|#%b{{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}}",
                $"{HeadsetEndpoint}|#%b{{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}}|1%b#",
                true)
        },
        {
            "svchost session that is not the sink ('Microphone' alone is not enough)",
            new(HeadsetEndpoint, "svchost", 3904, "Microphone (fifine Microphone)",
                $"{HeadsetEndpoint}|{SvchostPath}%b{ZeroGuid}", null, false)
        },
        {
            "svchost with the phone GUID but no A2DP SNK name (GUID is secondary only)",
            Phone(HeadsetEndpoint) with { DisplayName = string.Empty }
        },
        {
            "svchost with the phone GUID and a null name",
            Phone(HeadsetEndpoint) with { DisplayName = null }
        },
        {
            "non-svchost app whose title mentions A2DP SNK",
            new(HeadsetEndpoint, "chrome", 3904, "Microphone (Shlawg A2DP SNK)",
                $@"{HeadsetEndpoint}|\Device\HarddiskVolume3\chrome.exe%b{ZeroGuid}", null, false)
        },
        {
            "phone-like session flagged as system sounds",
            Phone(HeadsetEndpoint) with { IsSystemSoundsSession = true }
        },
        {
            "similar process name",
            Phone(HeadsetEndpoint) with { ProcessName = "svchost2.exe" }
        },
    };

    [Theory]
    [MemberData(nameof(PhoneSessions))]
    public void Matches_phone_sessions(string because, PhoneSessionInfo session)
    {
        PhoneSessionMatcher.IsPhoneSession(session).Should().BeTrue(because);
        PhoneSessionMatcher.Evaluate(session).Should().Be(PhoneSessionMatch.PrimaryAndGuid, because);
    }

    [Theory]
    [MemberData(nameof(NonPhoneSessions))]
    public void Rejects_other_sessions(string because, PhoneSessionInfo session)
    {
        PhoneSessionMatcher.IsPhoneSession(session).Should().BeFalse(because);
    }

    [Fact]
    public void Primary_rule_alone_matches_when_the_GUID_is_absent()
    {
        var session = Phone(HeadsetEndpoint) with
        {
            SessionIdentifier = $"{HeadsetEndpoint}|{SvchostPath}%b{ZeroGuid}",
            SessionInstanceIdentifier = null,
        };

        PhoneSessionMatcher.Evaluate(session).Should().Be(PhoneSessionMatch.Primary);
    }

    [Fact]
    public void GUID_check_is_case_insensitive()
    {
        var session = Phone(HeadsetEndpoint) with
        {
            SessionIdentifier = $"{HeadsetEndpoint}|{SvchostPath}%b{Guid.ToLowerInvariant()}",
            SessionInstanceIdentifier = null,
        };

        PhoneSessionMatcher.Evaluate(session).Should().Be(PhoneSessionMatch.PrimaryAndGuid);
    }

    [Fact]
    public void Finds_the_phone_session_on_every_endpoint_and_nothing_else()
    {
        // Phase 0 "sessions all" after switching the default to Speakers (Cirrus Logic).
        var all = new List<PhoneSessionInfo>
        {
            Phone(FifineEndpoint) with { ProcessId = 3904 },
            Phone(SpeakersEndpoint),
            Phone(HeadsetEndpoint),
        };
        all.AddRange(NonPhoneSessions.Select(row => (PhoneSessionInfo)row[1]));

        var found = PhoneSessionMatcher.FindPhoneSessions(all);

        found.Select(s => s.EndpointId).Should().BeEquivalentTo([FifineEndpoint, SpeakersEndpoint, HeadsetEndpoint]);
    }

    private static PhoneSessionInfo Phone(string endpoint) => new(
        endpoint,
        "svchost",
        3904,
        "Microphone (Shlawg A2DP SNK)",
        $"{endpoint}|{SvchostPath}%b{Guid}",
        $"{endpoint}|{SvchostPath}%b{Guid}|0%b3904",
        false);
}

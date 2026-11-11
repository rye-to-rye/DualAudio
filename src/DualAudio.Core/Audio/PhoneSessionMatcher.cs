using DualAudio.Core.Abstractions;

namespace DualAudio.Core.Audio;

public enum PhoneSessionMatch
{
    /// <summary>Not the phone stream.</summary>
    None,

    /// <summary>Primary rule matched (svchost + "A2DP SNK"); the session GUID was not present.</summary>
    Primary,

    /// <summary>Primary rule matched and the secondary session-GUID check agreed.</summary>
    PrimaryAndGuid,
}

/// <summary>
/// Identifies sessions carrying received phone audio (docs/phase0-findings.md, "Identification rule").
/// <list type="bullet">
/// <item>Primary: process image is svchost.exe AND DisplayName contains "A2DP SNK".</item>
/// <item>Secondary (confirmation only, never sufficient on its own): an identifier contains
/// <c>%b{2EA30F40-6E9F-4709-9327-A4E880941760}</c>.</item>
/// <item>Never matches on PID (shared svchost, changes on reboot) or on "Microphone" (localized).</item>
/// <item>Endpoint-agnostic: Windows keeps one phone session per render endpoint and all of them match.</item>
/// </list>
/// </summary>
public static class PhoneSessionMatcher
{
    public const string SinkMarker = "A2DP SNK";
    public const string HostProcess = "svchost";
    public const string PhoneSessionGuid = "{2EA30F40-6E9F-4709-9327-A4E880941760}";

    private const string GuidToken = "%b" + PhoneSessionGuid;

    public static PhoneSessionMatch Evaluate(PhoneSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.IsSystemSoundsSession
            || !IsHostProcess(session.ProcessName)
            || session.DisplayName is null
            || !session.DisplayName.Contains(SinkMarker, StringComparison.OrdinalIgnoreCase))
        {
            return PhoneSessionMatch.None;
        }

        return HasPhoneGuid(session.SessionIdentifier) || HasPhoneGuid(session.SessionInstanceIdentifier)
            ? PhoneSessionMatch.PrimaryAndGuid
            : PhoneSessionMatch.Primary;
    }

    public static bool IsPhoneSession(PhoneSessionInfo session) => Evaluate(session) != PhoneSessionMatch.None;

    /// <summary>All phone sessions, on every endpoint.</summary>
    public static IReadOnlyList<PhoneSessionInfo> FindPhoneSessions(IEnumerable<PhoneSessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Where(IsPhoneSession).ToList();
    }

    private static bool IsHostProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.AsSpan().Trim();
        var slash = name.LastIndexOfAny('\\', '/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Equals(HostProcess, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPhoneGuid(string? identifier) =>
        identifier is not null && identifier.Contains(GuidToken, StringComparison.OrdinalIgnoreCase);
}

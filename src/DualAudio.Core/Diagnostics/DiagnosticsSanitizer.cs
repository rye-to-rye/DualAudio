using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DualAudio.Core.Settings;

namespace DualAudio.Core.Diagnostics;

/// <summary>Information about this PC for a bug report. No user data.</summary>
/// <param name="WindowsVersion">E.g. "10.0.26200.7171 (25H2)".</param>
/// <param name="BluetoothAdapters">One per Bluetooth adapter; empty when none was found.</param>
public sealed record SystemInfo(string WindowsVersion, IReadOnlyList<BluetoothAdapterInfo> BluetoothAdapters);

/// <param name="Name">Adapter name as Device Manager shows it.</param>
public sealed record BluetoothAdapterInfo(string Name, string? DriverProvider, string? DriverVersion, string? DriverDate);

/// <summary>Reads <see cref="SystemInfo"/> (Platform: registry + DeviceInformation properties). Never throws.</summary>
public interface ISystemInfoProvider
{
    Task<SystemInfo> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Makes text and settings safe to put in a bug report: no full Bluetooth addresses, no nicknames.</summary>
public static partial class DiagnosticsSanitizer
{
    /// <summary>
    /// Replaces Bluetooth addresses with their last 4 hex digits ("…EEFF"): colon/dash forms
    /// ("5c:f3:70:aa:bb:cc") and 12-hex runs inside device instance ids ("&amp;5CF370AABBCC_"). Logs should never contain
    /// them; this is a second line of defence for the bug-report copy.
    /// </summary>
    public static string ScrubBluetoothAddresses(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scrubbed = SeparatedAddress().Replace(text, m => Tail(m.Value));
        return InstanceIdAddress().Replace(scrubbed, m => Tail(m.Value));
    }

    /// <summary>The settings as indented text, with nicknames removed and the saved phone's id redacted.</summary>
    public static string DescribeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sb = new StringBuilder();
        void Line(string key, object? value) =>
            sb.Append(key).Append(": ").AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-");

        Line("SchemaVersion", settings.SchemaVersion);
        Line("PreferredDeviceId", settings.PreferredDeviceId is null ? "-" : LogRedaction.DeviceId(settings.PreferredDeviceId));
        Line("DeviceNicknames", $"{settings.DeviceNicknames?.Count ?? 0} (removed)");
        Line("AutoConnectOnStart", settings.AutoConnectOnStart);
        Line("PhoneVolume", settings.PhoneVolume);
        Line("PhoneMuted", settings.PhoneMuted);
        Line("StartWithWindows", settings.StartWithWindows);
        Line("OnboardingCompleted", settings.OnboardingCompleted);
        Line("TrayNoticeShown", settings.TrayNoticeShown);
        foreach (var hotkey in settings.Hotkeys ?? [])
        {
            if (hotkey is not null)
            {
                Line("Hotkey " + hotkey.Action, $"{hotkey.Modifiers} + VK 0x{hotkey.VirtualKey:X2}");
            }
        }

        return sb.ToString();
    }

    private static string Tail(string value)
    {
        var hex = value.Where(char.IsAsciiHexDigit).ToArray();
        var keepPrefix = value.Length > 0 && !char.IsAsciiHexDigit(value[0]) ? value[0].ToString() : string.Empty;
        var keepSuffix = value.Length > 0 && !char.IsAsciiHexDigit(value[^1]) ? value[^1].ToString() : string.Empty;
        return keepPrefix + "…" + new string(hex[^4..]).ToUpperInvariant() + keepSuffix;
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])")]
    private static partial Regex SeparatedAddress();

    [GeneratedRegex(@"[&_\\#][0-9A-Fa-f]{12}(?=[_&\\#]|$)", RegexOptions.Multiline)]
    private static partial Regex InstanceIdAddress();
}

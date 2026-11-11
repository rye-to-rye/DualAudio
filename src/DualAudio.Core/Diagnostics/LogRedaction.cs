namespace DualAudio.Core.Diagnostics;

/// <summary>Keeps Bluetooth addresses out of logs.</summary>
public static class LogRedaction
{
    /// <summary>Reduces a device id (which embeds Bluetooth addresses) to its last 4 hex digits, e.g. "…EEFF".</summary>
    public static string DeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return "<none>";
        }

        Span<char> tail = stackalloc char[4];
        var found = 0;
        for (var i = deviceId.Length - 1; i >= 0 && found < 4; i--)
        {
            if (char.IsAsciiHexDigit(deviceId[i]))
            {
                tail[3 - found] = char.ToUpperInvariant(deviceId[i]);
                found++;
            }
        }

        return "…" + new string(tail[(4 - found)..]);
    }
}

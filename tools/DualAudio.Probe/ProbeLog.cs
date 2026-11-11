using System.Text;
using System.Text.RegularExpressions;

namespace DualAudio.Probe;

/// <summary>
/// Thread-safe, timestamped logger writing to the console and to probe-log.txt next to the exe.
/// </summary>
internal sealed class ProbeLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _file;
    private bool _disposed;

    public string FilePath { get; }

    private ProbeLog(string path)
    {
        FilePath = path;
        try
        {
            _file = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            _file.WriteLine();
            _file.WriteLine(new string('=', 80));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: cannot open log file '{path}': {ex.Message}. Logging to console only.");
            _file = null;
        }
    }

    public static ProbeLog CreateNextToExe() =>
        new(Path.Combine(AppContext.BaseDirectory, "probe-log.txt"));

    public void Info(string message) => Write("INF", message, ConsoleColor.Gray);
    public void Ok(string message) => Write("OK ", message, ConsoleColor.Green);
    public void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);
    public void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    /// <summary>Logs an exception with its HRESULT in hex. Never throws.</summary>
    public void Exception(string step, Exception ex) =>
        Error($"{step} failed: {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {OneLine(ex.Message)}");

    public static string OneLine(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(no message)" : s.ReplaceLineEndings(" ").Trim();

    private void Write(string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:00}] {message}";
        lock (_gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = previous;

            if (_disposed || _file is null) return;
            try { _file.WriteLine(line); }
            catch (IOException) { /* never crash because of the log file */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _file?.Dispose();
        }
    }
}

/// <summary>Helpers that keep Bluetooth addresses out of logs (only the last 4 hex chars survive).</summary>
internal static partial class Redact
{
    // Colon/dash separated MAC (aa:bb:cc:dd:ee:ff) or a bare 12-hex-digit run (aabbccddeeff).
    // The bare run excludes the last group of a GUID ("...-1234-0123456789ab"), which is also 12 hex
    // digits but is not an address; redacting it would hide audio session/endpoint identifier patterns.
    [GeneratedRegex(
        @"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])|(?<![0-9A-Fa-f])(?<![0-9A-Fa-f]{4}-)[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
    private static partial Regex MacLike();

    [GeneratedRegex("[^0-9A-Fa-f]")]
    private static partial Regex NonHex();

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public static string? Addresses(string? text) =>
        text is null ? null : MacLike().Replace(text, m => "**" + NonHex().Replace(m.Value, "")[^4..]);

    // Trailing "#{interface-class-guid}" shared by every device interface of the same class.
    [GeneratedRegex(@"#\{[0-9A-Fa-f\-]{36}\}$")]
    private static partial Regex InterfaceClassSuffix();

    /// <summary>
    /// The device-specific part of a device interface Id, after address redaction.
    /// Interface Ids look like <c>\\?\ENUMERATOR#hardware-id#instance-id#{interface-class-guid}</c>;
    /// the enumerator prefix and the class-GUID suffix are identical for all devices, so only the
    /// instance-id segment (which is where devices differ) is shown. Unknown formats fall back to
    /// the whole redacted Id.
    /// </summary>
    public static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(none)";
        var trimmed = InterfaceClassSuffix().Replace(id, "");
        var lastHash = trimmed.LastIndexOf('#');
        var specific = lastHash >= 0 && lastHash < trimmed.Length - 1 ? trimmed[(lastHash + 1)..] : trimmed;
        return Addresses(specific);
    }
}

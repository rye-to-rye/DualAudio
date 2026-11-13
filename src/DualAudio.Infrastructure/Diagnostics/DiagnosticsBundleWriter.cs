using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Settings;

namespace DualAudio.Infrastructure.Diagnostics;

/// <summary>Everything that goes into a bug report besides the log files.</summary>
/// <param name="StateLines">Recent structured <c>STATE</c> lines (oldest first).</param>
/// <param name="ResourcesLine">Latest <c>RESOURCES</c> line, if any.</param>
/// <param name="RedactTerms">Text to remove from the log copies (the user's nicknames).</param>
public sealed record DiagnosticsBundle(
    string AppVersion,
    DateTimeOffset CreatedAt,
    SystemInfo System,
    IReadOnlyList<string> OutputDevices,
    AppSettings Settings,
    IReadOnlyList<string> StateLines,
    string? ResourcesLine,
    IReadOnlyCollection<string> RedactTerms);

/// <summary>
/// Writes <c>DualAudio-diagnostics-&lt;UTC time&gt;.zip</c>: <c>diagnostics.txt</c> (version, Windows, Bluetooth adapters
/// and drivers, outputs, resources, settings without nicknames, recent STATE lines) and <c>logs\</c> with the newest
/// <see cref="LogFileCount"/> log files. Only files from DualAudio's own log folder are read. Copies are scrubbed:
/// Bluetooth addresses → last 4 hex digits, nicknames → "&lt;nickname&gt;", the user profile path → %USERPROFILE%.
/// </summary>
public sealed class DiagnosticsBundleWriter(AppPaths paths)
{
    public const int LogFileCount = 3;
    public const string FilePrefix = "DualAudio-diagnostics-";

    /// <summary>Creates the zip in <paramref name="targetDirectory"/> and returns its full path.</summary>
    public async Task<string> WriteAsync(string targetDirectory, DiagnosticsBundle bundle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(bundle);
        Directory.CreateDirectory(targetDirectory);

        var stamp = bundle.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(targetDirectory, $"{FilePrefix}{stamp}.zip");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(targetDirectory, $"{FilePrefix}{stamp}-{n}.zip");
        }

        var scrub = CreateScrubber(bundle.RedactTerms);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            await WriteEntryAsync(zip, "diagnostics.txt", scrub(Describe(bundle)), cancellationToken).ConfigureAwait(false);
            foreach (var log in NewestLogFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteEntryAsync(zip, "logs/" + log.Name, scrub(await ReadSharedAsync(log.FullName, cancellationToken).ConfigureAwait(false)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    /// <summary>The newest log files in DualAudio's own log folder (top level, *.log only).</summary>
    public IReadOnlyList<FileInfo> NewestLogFiles()
    {
        var dir = new DirectoryInfo(paths.LogDirectory);
        return dir.Exists
            ? [.. dir.EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).Take(LogFileCount)]
            : [];
    }

    internal static string Describe(DiagnosticsBundle b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DualAudio diagnostics");
        sb.AppendLine("=====================");
        sb.Append("App version:     ").AppendLine(b.AppVersion);
        sb.Append("Created (UTC):   ").AppendLine(b.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("Windows:         ").AppendLine(b.System.WindowsVersion);
        sb.AppendLine();
        sb.AppendLine("Bluetooth adapters:");
        if (b.System.BluetoothAdapters.Count == 0)
        {
            sb.AppendLine("  (none found)");
        }

        foreach (var a in b.System.BluetoothAdapters)
        {
            sb.Append("  ").Append(a.Name)
              .Append(" | driver ").Append(a.DriverVersion ?? "?")
              .Append(" | provider ").Append(a.DriverProvider ?? "?")
              .Append(" | date ").AppendLine(a.DriverDate ?? "?");
        }

        sb.AppendLine();
        sb.AppendLine("Output devices:");
        if (b.OutputDevices.Count == 0)
        {
            sb.AppendLine("  (none found)");
        }

        foreach (var o in b.OutputDevices)
        {
            sb.Append("  ").AppendLine(o);
        }

        sb.AppendLine();
        sb.Append("Resources: ").AppendLine(b.ResourcesLine ?? "-");
        sb.AppendLine();
        sb.AppendLine("Settings (nicknames removed):");
        foreach (var line in DiagnosticsSanitizer.DescribeSettings(b.Settings).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append("  ").AppendLine(line);
        }

        sb.AppendLine();
        sb.Append("Last ").Append(b.StateLines.Count).AppendLine(" STATE lines:");
        foreach (var line in b.StateLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    internal static Func<string, string> CreateScrubber(IReadOnlyCollection<string> redactTerms)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var terms = redactTerms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 2)
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length)
            .Select(t => new Regex(@"(?<!\w)" + Regex.Escape(t) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        return text =>
        {
            var result = DiagnosticsSanitizer.ScrubBluetoothAddresses(text);
            if (!string.IsNullOrEmpty(profile))
            {
                result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var term in terms)
            {
                result = term.Replace(result, "<nickname>");
            }

            return result;
        };
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        // Serilog keeps today's file open for writing.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

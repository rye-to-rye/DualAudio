using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Startup;

/// <summary>
/// The per-user "start with Windows" entry (Windows: value "DualAudio" under HKCU\...\CurrentVersion\Run).
/// Implementations may throw; <see cref="AutoStartRegistration"/> handles that.
/// </summary>
public interface IAutoStartEntry
{
    /// <summary>The current command line, or null when there is no entry.</summary>
    string? Read();

    void Write(string command);

    /// <summary>Removes the entry; no-op when there is none.</summary>
    void Remove();
}

public enum AutoStartSyncResult
{
    Unchanged,
    Written,
    Removed,
    Failed,
}

/// <summary>
/// Keeps the auto-start entry in line with the setting. Called on every start (so a moved exe gets its new path) and
/// when the setting is toggled. Never throws.
/// </summary>
public sealed class AutoStartRegistration(IAutoStartEntry entry, ILogger<AutoStartRegistration>? logger = null)
{
    /// <summary>Command-line switch: start straight to the tray (no window).</summary>
    public const string MinimizedArgument = "--minimized";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>"\"C:\path\DualAudio.exe\" --minimized" (the path is always quoted: it may contain spaces).</summary>
    public static string CommandFor(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return $"\"{exePath.Trim().Trim('"')}\" {MinimizedArgument}";
    }

    public static bool IsMinimized(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, MinimizedArgument, StringComparison.OrdinalIgnoreCase));

    public AutoStartSyncResult Sync(bool enabled, string? exePath)
    {
        try
        {
            var current = entry.Read();
            if (!enabled)
            {
                if (current is null)
                {
                    return AutoStartSyncResult.Unchanged;
                }

                entry.Remove();
                _logger.LogInformation("Start with Windows: entry removed");
                return AutoStartSyncResult.Removed;
            }

            if (string.IsNullOrWhiteSpace(exePath))
            {
                _logger.LogWarning("Start with Windows: the exe path is unknown; entry not written");
                return AutoStartSyncResult.Failed;
            }

            var desired = CommandFor(exePath);
            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            {
                return AutoStartSyncResult.Unchanged;
            }

            entry.Write(desired);
            _logger.LogInformation("Start with Windows: entry {What}", current is null ? "written" : "updated (exe moved?)");
            return AutoStartSyncResult.Written;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Start with Windows: updating the entry failed");
            return AutoStartSyncResult.Failed;
        }
    }
}

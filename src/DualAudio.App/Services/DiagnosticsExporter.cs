using System.Reflection;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Diagnostics;
using DualAudio.Infrastructure;
using DualAudio.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.Services;

public static class AppInfo
{
    /// <summary>"1.2.3" (informational version without the commit hash).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "?";
}

/// <summary>"Copy diagnostics": builds the bug-report zip on the Desktop.</summary>
public interface IDiagnosticsExporter
{
    /// <summary>Creates the zip and returns its full path. Runs off the UI thread; may throw (the caller reports it).</summary>
    Task<string> ExportAsync(CancellationToken cancellationToken = default);
}

public sealed class DiagnosticsExporter(
    DiagnosticsBundleWriter writer,
    ISettingsStore settings,
    IOutputDeviceService outputs,
    ISystemInfoProvider system,
    IClock clock,
    AppPaths paths,
    ConnectionDiagnostics? stateLines = null,
    ResourceMonitor? resources = null,
    ILogger<DiagnosticsExporter>? logger = null) : IDiagnosticsExporter
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating a diagnostics zip");
        var current = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Sanitize();
        var endpoints = await outputs.GetRenderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var info = await system.GetAsync(cancellationToken).ConfigureAwait(false);

        var bundle = new DiagnosticsBundle(
            AppInfo.Version,
            clock.UtcNow,
            info,
            [.. endpoints.Select(e => e.IsDefault ? e.Name + " (default)" : e.Name)],
            current,
            stateLines?.RecentLines ?? [],
            resources?.LatestLine,
            [.. current.DeviceNicknames.Values]);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var target = string.IsNullOrEmpty(desktop) ? paths.RootDirectory : desktop;
        var path = await writer.WriteAsync(target, bundle, cancellationToken).ConfigureAwait(false);

        // File name only: the full path contains the Windows user name.
        _logger.LogInformation("Diagnostics zip created: {File}", System.IO.Path.GetFileName(path));
        return path;
    }
}

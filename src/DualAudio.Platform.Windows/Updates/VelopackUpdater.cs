using DualAudio.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace DualAudio.Platform.Windows.Updates;

/// <summary>
/// <see cref="IAppUpdater"/> over Velopack with the public GitHub Releases of the repo (no token). When the app isn't an
/// installed Velopack copy (dotnet run, a build folder) <see cref="IsInstalled"/> is false and nothing else is called.
/// </summary>
public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepoUrl = "https://github.com/rye-to-rye/DualAudio";

    /// <summary>
    /// Velopack pack id = install folder name (%LOCALAPPDATA%\DualAudioApp). Must NOT be "DualAudio": that folder holds
    /// settings and logs, and Velopack deletes its whole install folder on uninstall.
    /// </summary>
    public const string PackId = "DualAudioApp";

    private readonly UpdateManager? _manager;
    private readonly ILogger _logger;

    public VelopackUpdater(ILogger<VelopackUpdater>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Velopack update manager unavailable (not an installed copy?)");
        }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string? CurrentVersion => IsInstalled ? _manager!.CurrentVersion?.ToString() : null;

    public AvailableUpdate? PendingRestart =>
        IsInstalled && _manager!.UpdatePendingRestart is { } asset ? new AvailableUpdate(asset.Version.ToString(), asset) : null;

    /// <summary>
    /// The installed exe at Velopack's stable path (%LOCALAPPDATA%\DualAudioApp\current\DualAudio.exe): the "current"
    /// folder is replaced in place on update, so a Run value pointing there survives updates. Null when not installed.
    /// </summary>
    public static string? StableExePath()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet)
            {
                return null;
            }

            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion is null || string.IsNullOrEmpty(locator.AppContentDir)
                || string.IsNullOrEmpty(locator.ThisExeRelativePath))
            {
                return null;
            }

            var path = Path.Combine(locator.AppContentDir, locator.ThisExeRelativePath);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var manager = Installed();
        var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return info is null ? null : new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Handle is not UpdateInfo info)
        {
            return Task.CompletedTask; // a pending (already downloaded) asset
        }

        return Installed().DownloadUpdatesAsync(info, progress: null, cancellationToken);
    }

    public void ApplyAfterExit(AvailableUpdate update, bool restart)
    {
        ArgumentNullException.ThrowIfNull(update);
        var asset = update.Handle switch
        {
            UpdateInfo info => info.TargetFullRelease,
            VelopackAsset a => a,
            _ => throw new ArgumentException("Not a Velopack update.", nameof(update)),
        };

        // Update.exe waits for this process to exit, applies silently, and restarts only when asked.
        Installed().WaitExitThenApplyUpdates(asset, silent: true, restart: restart, restartArgs: []);
        _logger.LogInformation("Velopack will apply {Version} after exit (restart: {Restart})", asset.Version, restart);
    }

    private UpdateManager Installed() =>
        IsInstalled ? _manager! : throw new InvalidOperationException("Not an installed copy.");
}

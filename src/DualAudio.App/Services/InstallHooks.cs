using DualAudio.Core.Startup;
using DualAudio.Platform.Windows.Startup;
using DualAudio.Platform.Windows.Updates;

namespace DualAudio.App.Services;

/// <summary>
/// Velopack hooks. Each runs in a short-lived DualAudio process started by the installer (Update.exe), before the
/// single-instance mutex, the host or any window; it must finish quickly and never throw.
/// <list type="bullet">
/// <item>After install: nothing (no callback registered). Onboarding runs on the first normal launch, as before.</item>
/// <item>Before update: ask a running DualAudio to exit cleanly so files can be replaced (it releases the phone link
/// and removes its tray icon instead of being killed).</item>
/// <item>Before uninstall: the same, then remove our HKCU Run value. Settings and logs in %LOCALAPPDATA%\DualAudio are
/// kept: Velopack only deletes its own install folder (%LOCALAPPDATA%\DualAudioApp), and there is no "remove data" choice.</item>
/// </list>
/// </summary>
public static class InstallHooks
{
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    public static void BeforeUpdate() => StopRunningInstance(SingleInstanceGuard.RequestRunningInstanceToExit);

    public static void BeforeUninstall() =>
        BeforeUninstall(new RunKeyAutoStartEntry(), SingleInstanceGuard.RequestRunningInstanceToExit);

    /// <summary>Testable core of the uninstall hook.</summary>
    internal static void BeforeUninstall(IAutoStartEntry runValue, Func<TimeSpan, bool> requestExit)
    {
        StopRunningInstance(requestExit);
        try
        {
            new AutoStartRegistration(runValue).Sync(enabled: false, exePath: null);
        }
        catch (Exception)
        {
            // Sync already logs and swallows; nothing else may escape into the installer.
        }
    }

    private static void StopRunningInstance(Func<TimeSpan, bool> requestExit)
    {
        try
        {
            requestExit(ExitTimeout);
        }
        catch (Exception)
        {
            // If it can't be reached, Velopack ends the process itself.
        }
    }
}

/// <summary>Which exe path the start-with-Windows Run value points at.</summary>
public static class RunKeyPath
{
    /// <summary>
    /// Installed: Velopack's stable %LOCALAPPDATA%\DualAudioApp\current\DualAudio.exe (survives updates). Otherwise the
    /// running exe (dev builds).
    /// </summary>
    public static string? Current() => Choose(VelopackUpdater.StableExePath(), Environment.ProcessPath);

    internal static string? Choose(string? stableInstalledExe, string? processPath) =>
        string.IsNullOrWhiteSpace(stableInstalledExe) ? processPath : stableInstalledExe;
}

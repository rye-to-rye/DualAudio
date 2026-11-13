namespace DualAudio.Infrastructure;

/// <summary>Where DualAudio keeps its files: %LOCALAPPDATA%\DualAudio (settings.json, logs\).</summary>
public sealed record AppPaths(string RootDirectory)
{
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio"));
}

namespace DualAudio.App.ViewModels;

public sealed record PackageLicense(string Package, string License, string Url);

/// <summary>
/// NuGet packages DualAudio ships with (direct references; licenses taken from each package's nuspec).
/// Keep in sync with the csproj files when a package is added or removed.
/// </summary>
public static class ThirdPartyNotices
{
    private const string Mit = "https://licenses.nuget.org/MIT";
    private const string Apache = "https://licenses.nuget.org/Apache-2.0";

    public static IReadOnlyList<PackageLicense> Packages { get; } =
    [
        new("CommunityToolkit.Mvvm", "MIT", Mit),
        new("H.NotifyIcon.Wpf", "MIT", Mit),
        new("Microsoft.Extensions.Hosting / Logging / DependencyInjection", "MIT", Mit),
        new("NAudio.Wasapi", "MIT", Mit),
        new("Serilog.Extensions.Hosting", "Apache-2.0", Apache),
        new("Serilog.Sinks.File", "Apache-2.0", Apache),
        new("WPF-UI", "MIT", Mit),
    ];
}

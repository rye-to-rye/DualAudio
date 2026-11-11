using System.Reflection;
using System.Runtime.Versioning;
using DualAudio.Core.Connection;

namespace DualAudio.Core.Tests.Architecture;

public sealed class CoreIsPlatformIndependentTests
{
    private static readonly Assembly Core = typeof(ConnectionCoordinator).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    [
        "Windows",              // WinRT projections (Windows.winmd, Windows.Foundation...)
        "Microsoft.Windows",    // CsWin32, Microsoft.Windows.SDK.NET
        "WinRT",                // CsWinRT runtime
        "Microsoft.Win32",
        "System.Windows",       // WinForms
        "PresentationCore",     // WPF
        "PresentationFramework",
        "WindowsBase",
        "Wpf.Ui",
        "H.NotifyIcon",
        "NAudio",
        "System.Management",
    ];

    [Fact]
    public void Core_references_no_Windows_assemblies()
    {
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        referenced.Should().NotBeEmpty();
        referenced.Should().NotContain(
            name => ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
            "Core must stay platform-independent; platform code belongs in DualAudio.Platform.Windows");
    }

    [Fact]
    public void Core_targets_plain_net_without_an_OS_platform()
    {
        Core.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName.Should().Be(".NETCoreApp,Version=v10.0");
        Core.GetCustomAttribute<TargetPlatformAttribute>().Should().BeNull();
        Core.GetCustomAttribute<SupportedOSPlatformAttribute>().Should().BeNull();
    }
}

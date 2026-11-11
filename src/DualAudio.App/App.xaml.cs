using System.Windows;

namespace DualAudio.App;

/// <summary>
/// Skeleton only. The tray UI, hosting and DI wiring arrive in a later task; until then the
/// app exits immediately so it never lingers as an invisible process.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}

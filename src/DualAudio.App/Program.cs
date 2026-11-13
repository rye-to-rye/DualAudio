using DualAudio.App.Composition;
using DualAudio.App.Services;
using DualAudio.Core.Startup;
using DualAudio.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;

namespace DualAudio.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // FIRST, before the mutex, the host and onboarding: when the installer runs us for a hook it handles it and
        // exits here. Downloaded updates are NOT applied at start-up (that could restart the app in the middle of a
        // logon or a second launch); UpdateService applies them on Exit or "Restart now".
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUpdateFastCallback(_ => InstallHooks.BeforeUpdate())
            .OnBeforeUninstallFastCallback(_ => InstallHooks.BeforeUninstall())
            .Run();

        using var instance = SingleInstanceGuard.TryAcquire();
        if (instance is null)
        {
            // Another DualAudio is running; it has been asked to show its window.
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders(); // Serilog (file) only; registered by AddDualAudioInfrastructure.
        builder.Services.AddDualAudioApp(AppPaths.ForCurrentUser());

        // Disposed asynchronously by App during shutdown (coordinator/receiver release is awaited there).
        var host = builder.Build();

        // "--minimized" (Start with Windows): straight to the tray, no window unless onboarding is needed.
        var app = new App(host, instance, startMinimized: AutoStartRegistration.IsMinimized(args));
        app.InitializeComponent();
        return app.Run();
    }
}

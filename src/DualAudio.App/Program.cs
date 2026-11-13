using DualAudio.App.Composition;
using DualAudio.App.Services;
using DualAudio.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DualAudio.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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

        var app = new App(host, instance);
        app.InitializeComponent();
        return app.Run();
    }
}

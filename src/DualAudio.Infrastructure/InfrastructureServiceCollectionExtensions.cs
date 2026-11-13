using DualAudio.Core.Abstractions;
using DualAudio.Infrastructure.Diagnostics;
using DualAudio.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace DualAudio.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Registers <see cref="AppPaths"/>, the JSON <see cref="ISettingsStore"/> and Serilog file logging
    /// (rolling daily, 7 files, 10 MB each) in <see cref="AppPaths.LogDirectory"/>.
    /// Device ids are redacted by the callers (LogRedaction); nothing here logs audio content.
    /// </summary>
    public static IServiceCollection AddDualAudioInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddLogging(); // ILogger<T>; Serilog below replaces the factory.
        services.TryAddSingleton(paths);
        services.TryAddSingleton(sp => new JsonSettingsStore(paths.SettingsFile, sp.GetService<ILogger<JsonSettingsStore>>()));
        services.TryAddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());
        services.TryAddSingleton<DiagnosticsBundleWriter>(); // "Copy diagnostics": reads only paths.LogDirectory

        services.AddSerilog(
            (_, config) => config
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(paths.LogDirectory, "dualaudio-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    outputTemplate: FileTemplate));
        return services;
    }
}

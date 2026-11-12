using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;

namespace DualAudio.Probe;

/// <summary>
/// Settings for the `coord` harness only: kept in memory for the life of the probe (the JSON store in
/// Infrastructure is a later task). Enough to verify "setting re-applied after disconnect + reconnect".
/// </summary>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly object _sync = new();
    private AppSettings _settings = AppSettings.Default;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return Task.FromResult(_settings);
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _settings = settings;
        }

        return Task.CompletedTask;
    }
}

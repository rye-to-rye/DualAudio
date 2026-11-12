using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;

namespace DualAudio.Core.Tests.Fakes;

internal sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    private readonly object _sync = new();
    private AppSettings _settings = initial ?? AppSettings.Default;

    public int SaveCount { get; private set; }

    public AppSettings Current
    {
        get { lock (_sync) { return _settings; } }
    }

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
            SaveCount++;
        }

        return Task.CompletedTask;
    }
}

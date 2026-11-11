using DualAudio.Core.Abstractions;

namespace DualAudio.Core.Connection;

/// <summary>
/// Devices currently reported by the watcher. Names come only from DeviceAdded; Updated events are
/// ignored because Phase 0 saw them report garbage (Name="4", IsConnected=n/a). Thread-safe.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PhoneDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public void Add(PhoneDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_sync)
        {
            _devices[device.Id] = device;
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_sync)
        {
            return _devices.Remove(deviceId);
        }
    }

    public bool Contains(string deviceId)
    {
        lock (_sync)
        {
            return _devices.ContainsKey(deviceId);
        }
    }

    public PhoneDevice? Find(string deviceId)
    {
        lock (_sync)
        {
            return _devices.GetValueOrDefault(deviceId);
        }
    }

    public IReadOnlyList<PhoneDevice> Snapshot()
    {
        lock (_sync)
        {
            return _devices.Values.ToList();
        }
    }
}

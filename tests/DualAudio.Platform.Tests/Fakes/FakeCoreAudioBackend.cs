using DualAudio.Core.Abstractions;
using DualAudio.Platform.Windows.Audio;

namespace DualAudio.Platform.Tests.Fakes;

/// <summary>
/// In-memory Core Audio: endpoints with sessions whose volume/mute persist across handles (like real sessions).
/// Mutate from the test thread only while the controller is idle (after FlushAsync).
/// </summary>
internal sealed class FakeCoreAudioBackend : ICoreAudioBackend
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<FakeSession>> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultId;

    public event EventHandler<string>? SessionCreated;

    public event EventHandler? EndpointsChanged;

    public event EventHandler? DefaultEndpointChanged;

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public int LiveHandles { get; private set; }

    public void AddEndpoint(string id, bool isDefault = false)
    {
        lock (_sync)
        {
            _endpoints[id] = [];
            if (isDefault)
            {
                _defaultId = id;
            }
        }
    }

    public void RemoveEndpoint(string id)
    {
        lock (_sync)
        {
            _endpoints.Remove(id);
        }
    }

    public FakeSession AddSession(string endpointId, FakeSession session)
    {
        lock (_sync)
        {
            _endpoints[endpointId].Add(session);
        }

        return session;
    }

    public void RemoveSession(string endpointId, FakeSession session)
    {
        lock (_sync)
        {
            _endpoints[endpointId].Remove(session);
        }
    }

    public void SetDefault(string id)
    {
        lock (_sync)
        {
            _defaultId = id;
        }
    }

    public void RaiseSessionCreated(string endpointId) => SessionCreated?.Invoke(this, endpointId);

    public void RaiseEndpointsChanged() => EndpointsChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseDefaultChanged() => DefaultEndpointChanged?.Invoke(this, EventArgs.Empty);

    public void Start() => Started = true;

    public string? GetDefaultRenderEndpointId()
    {
        lock (_sync)
        {
            return _defaultId;
        }
    }

    public IReadOnlyList<string> GetActiveRenderEndpointIds()
    {
        lock (_sync)
        {
            return [.. _endpoints.Keys];
        }
    }

    public IReadOnlyList<IAudioSessionHandle> GetSessions(string endpointId)
    {
        lock (_sync)
        {
            if (!_endpoints.TryGetValue(endpointId, out var sessions))
            {
                return [];
            }

            LiveHandles += sessions.Count;
            return [.. sessions.Select(s => new Handle(this, endpointId, s))];
        }
    }

    public void Dispose() => Disposed = true;

    private void Released()
    {
        lock (_sync)
        {
            LiveHandles--;
        }
    }

    /// <summary>A session: phone or not, decided only by its metadata (the controller must use PhoneSessionMatcher).</summary>
    internal sealed class FakeSession(string processName, string displayName, string? sessionIdentifier = null)
    {
        private readonly List<Handle> _listeners = [];

        public string ProcessName { get; } = processName;

        public string DisplayName { get; } = displayName;

        public string InstanceId { get; } = (sessionIdentifier ?? "id") + "|" + Guid.NewGuid().ToString("N");

        public string? SessionIdentifier { get; } = sessionIdentifier;

        public bool Active { get; set; }

        public float Volume { get; set; } = 1f;

        public bool Mute { get; set; }

        public float Peak { get; set; }

        public bool Expired { get; set; }

        public int Writes { get; set; }

        public static FakeSession Phone() =>
            new("svchost", "Microphone (Shlawg A2DP SNK)", "{x}|\\Windows\\System32\\svchost.exe%b{2EA30F40-6E9F-4709-9327-A4E880941760}");

        public static FakeSession App(string name = "chrome") => new(name, string.Empty, $"{{x}}|{name}.exe%b{{00000000-0000-0000-0000-000000000000}}");

        public void RaiseStateChanged()
        {
            List<Handle> listeners;
            lock (_listeners)
            {
                listeners = [.. _listeners];
            }

            foreach (var handle in listeners)
            {
                handle.Raise();
            }
        }

        public void Listen(Handle handle, bool on)
        {
            lock (_listeners)
            {
                if (on)
                {
                    _listeners.Add(handle);
                }
                else
                {
                    _listeners.Remove(handle);
                }
            }
        }
    }

    internal sealed class Handle(FakeCoreAudioBackend owner, string endpointId, FakeSession session) : IAudioSessionHandle
    {
        private bool _disposed;

        public event EventHandler? StateChanged;

        public PhoneSessionInfo Info { get; } = new(endpointId, session.ProcessName, 1234, session.DisplayName, session.SessionIdentifier, session.InstanceId, false);

        public bool IsActive => Check().Active;

        public bool IsExpired => Check().Expired;

        public float Volume
        {
            get => Check().Volume;
            set
            {
                Check().Volume = value;
                session.Writes++;
            }
        }

        public bool Mute
        {
            get => Check().Mute;
            set
            {
                Check().Mute = value;
                session.Writes++;
            }
        }

        public float Peak => Check().Peak;

        public void EnableNotifications() => session.Listen(this, on: true);

        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session.Listen(this, on: false);
            owner.Released();
        }

        private FakeSession Check()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return session;
        }
    }
}

using System.Diagnostics;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// <see cref="ICoreAudioBackend"/> over NAudio.Wasapi (documented Core Audio APIs only). Must be used from one
/// MTA thread. Keeps one <see cref="MMDevice"/> per active render endpoint alive so its
/// <see cref="AudioSessionManager.OnSessionCreated"/> registration stays in place
/// (IAudioSessionNotification only fires after the endpoint's sessions have been enumerated once).
/// </summary>
internal sealed class NAudioCoreAudioBackend : ICoreAudioBackend
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, EndpointEntry> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private bool _disposed;

    public NAudioCoreAudioBackend(ILogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<string>? SessionCreated;

    public event EventHandler? EndpointsChanged;

    public event EventHandler? DefaultEndpointChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enumerator is not null)
        {
            return;
        }

        _enumerator = new MMDeviceEnumerator();

        // NAudio wraps IMMNotificationClient; false = raise on the audio worker thread (handlers only post work).
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += (_, _) => EndpointsChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceRemoved += (_, _) => EndpointsChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceStateChanged += (_, _) => EndpointsChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
            {
                DefaultEndpointChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public string? GetDefaultRenderEndpointId()
    {
        var enumerator = Enumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    public IReadOnlyList<string> GetActiveRenderEndpointIds()
    {
        var enumerator = Enumerator();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                var id = device.ID;
                seen.Add(id);
                if (_endpoints.ContainsKey(id))
                {
                    device.Dispose();
                    continue;
                }

                _endpoints[id] = new EndpointEntry(this, id, device);
            }
        }

        foreach (var gone in _endpoints.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _endpoints[gone].Dispose();
            _endpoints.Remove(gone);
        }

        return [.. seen];
    }

    public IReadOnlyList<IAudioSessionHandle> GetSessions(string endpointId)
    {
        if (!_endpoints.TryGetValue(endpointId, out var entry))
        {
            return [];
        }

        var manager = entry.Manager;
        manager.RefreshSessions();
        var collection = manager.Sessions;
        var handles = new List<IAudioSessionHandle>(collection.Count);
        var processNames = new Dictionary<uint, string>();
        for (var i = 0; i < collection.Count; i++)
        {
            AudioSessionControl control;
            try
            {
                control = collection[i];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading session {Index} failed", i);
                continue;
            }

            try
            {
                handles.Add(new SessionHandle(endpointId, control, processNames));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading session {Index} metadata failed", i);
                control.Dispose();
            }
        }

        return handles;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _endpoints.Values)
        {
            entry.Dispose();
        }

        _endpoints.Clear();
        _notifications?.Dispose();
        _notifications = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private MMDeviceEnumerator Enumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _enumerator ?? throw new InvalidOperationException("Start() was not called.");
    }

    private sealed class EndpointEntry : IDisposable
    {
        private readonly NAudioCoreAudioBackend _owner;
        private readonly string _id;
        private readonly MMDevice _device;
        private readonly AudioSessionManager.SessionCreatedDelegate _onSessionCreated;

        public EndpointEntry(NAudioCoreAudioBackend owner, string id, MMDevice device)
        {
            _owner = owner;
            _id = id;
            _device = device;
            Manager = device.AudioSessionManager; // cached and disposed by the MMDevice
            // Audio-stack thread: only signal; the controller rescans on its worker. The session argument is borrowed.
            _onSessionCreated = (_, _) => _owner.SessionCreated?.Invoke(_owner, _id);
            Manager.OnSessionCreated += _onSessionCreated;
        }

        public AudioSessionManager Manager { get; }

        public void Dispose()
        {
            Manager.OnSessionCreated -= _onSessionCreated;
            _device.Dispose();
        }
    }

    private sealed class SessionHandle : IAudioSessionHandle, IAudioSessionEventsHandler
    {
        private readonly AudioSessionControl _control;
        private volatile bool _expired;
        private bool _notifying;
        private bool _disposed;

        public SessionHandle(string endpointId, AudioSessionControl control, Dictionary<uint, string> processNames)
        {
            _control = control;
            var pid = control.GetProcessID;
            Info = new PhoneSessionInfo(
                EndpointId: endpointId,
                ProcessName: ProcessName(pid, processNames),
                ProcessId: (int)pid,
                DisplayName: control.DisplayName,
                SessionIdentifier: control.GetSessionIdentifier,
                SessionInstanceIdentifier: control.GetSessionInstanceIdentifier,
                IsSystemSoundsSession: control.IsSystemSoundsSession);
        }

        public event EventHandler? StateChanged;

        public PhoneSessionInfo Info { get; }

        public bool IsActive => _control.State == AudioSessionState.AudioSessionStateActive;

        public bool IsExpired => _expired || _control.State == AudioSessionState.AudioSessionStateExpired;

        public float Volume
        {
            get => _control.SimpleAudioVolume.Volume;
            set => _control.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
        }

        public bool Mute
        {
            get => _control.SimpleAudioVolume.Mute;
            set => _control.SimpleAudioVolume.Mute = value;
        }

        public float Peak => _control.AudioMeterInformation.MasterPeakValue;

        public void EnableNotifications()
        {
            if (_notifying)
            {
                return;
            }

            _control.RegisterEventClient(this);
            _notifying = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_notifying)
                {
                    _control.UnRegisterEventClient(this);
                }
            }
            catch (Exception)
            {
                // Session already gone; unregistering is best effort.
            }

            _control.Dispose();
        }

        // ---- IAudioSessionEventsHandler (audio-stack thread: only signal)

        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                _expired = true;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            _expired = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OnVolumeChanged(float volume, bool isMuted)
        {
        }

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }

        private static string ProcessName(uint pid, Dictionary<uint, string> cache)
        {
            if (cache.TryGetValue(pid, out var name))
            {
                return name;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (Exception)
            {
                name = string.Empty; // exited, or access denied: cannot be the phone session then
            }

            cache[pid] = name;
            return name;
        }
    }
}

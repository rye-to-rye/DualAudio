using DualAudio.Core.Abstractions;

namespace DualAudio.Platform.Windows.Audio;

/// <summary>
/// Thin seam over Core Audio so <see cref="NAudioSessionController"/>'s logic is unit-testable with fakes.
/// Every member except the events is called on the controller's <see cref="MtaWorker"/> thread only.
/// Events may be raised on any thread and handlers must not call back into the backend synchronously.
/// </summary>
internal interface ICoreAudioBackend : IDisposable
{
    /// <summary>A new session was created on the endpoint with this id.</summary>
    event EventHandler<string>? SessionCreated;

    /// <summary>A render endpoint was added, removed or changed state.</summary>
    event EventHandler? EndpointsChanged;

    /// <summary>The default render endpoint for the Multimedia role changed.</summary>
    event EventHandler? DefaultEndpointChanged;

    /// <summary>Creates the enumerator and registers notifications. Called once before anything else.</summary>
    void Start();

    string? GetDefaultRenderEndpointId();

    IReadOnlyList<string> GetActiveRenderEndpointIds();

    /// <summary>Fresh handles for every session on the endpoint; the caller disposes each one.</summary>
    IReadOnlyList<IAudioSessionHandle> GetSessions(string endpointId);
}

/// <summary>One session on one endpoint. Disposing it unregisters its callbacks and releases the COM object.</summary>
internal interface IAudioSessionHandle : IDisposable
{
    /// <summary>Raised (any thread) when the session's state changes or it is disconnected/expires.</summary>
    event EventHandler? StateChanged;

    /// <summary>Metadata captured when the handle was created.</summary>
    PhoneSessionInfo Info { get; }

    bool IsActive { get; }

    /// <summary>Gone for good (Expired state, or OnSessionDisconnected arrived).</summary>
    bool IsExpired { get; }

    /// <summary>0..1.</summary>
    float Volume { get; set; }

    bool Mute { get; set; }

    /// <summary>0..1.</summary>
    float Peak { get; }

    /// <summary>Subscribe to state notifications. Only called for sessions that are kept (phone sessions).</summary>
    void EnableNotifications();
}

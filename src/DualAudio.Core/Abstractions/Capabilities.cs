namespace DualAudio.Core.Abstractions;

/// <summary>
/// What onboarding checks before the user pairs a phone. Built by <see cref="ICapabilityService"/>.
/// </summary>
/// <param name="OsBuild">Windows build number (e.g. 26200).</param>
/// <param name="IsOsSupported">Build is at least <see cref="MinimumOsBuild"/> and AudioPlaybackConnection exists.</param>
/// <param name="Radio">Aggregate Bluetooth radio state.</param>
/// <param name="AudioSourceCount">Paired devices the A2DP-source watcher reports (phones that can stream to this PC).</param>
/// <param name="IsDeviceListComplete">The watcher finished its initial enumeration, so <paramref name="AudioSourceCount"/> is final.</param>
public sealed record CapabilityReport(
    int OsBuild,
    bool IsOsSupported,
    RadioStatus Radio,
    int AudioSourceCount,
    bool IsDeviceListComplete)
{
    /// <summary>Windows 10 2004: first build with Windows.Media.Audio.AudioPlaybackConnection.</summary>
    public const int MinimumOsBuild = 19041;

    public bool HasRadio => Radio is not RadioStatus.NotPresent;

    public bool IsRadioOn => Radio == RadioStatus.On;

    public bool HasAudioSource => AudioSourceCount > 0;

    /// <summary>Hard requirements for continuing onboarding (a phone is paired in a later step).</summary>
    public bool CanContinue => IsOsSupported && IsRadioOn;
}

/// <summary>Checks whether this PC can receive phone audio. Never throws except for cancellation.</summary>
public interface ICapabilityService
{
    Task<CapabilityReport> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The name phones see when they look for this PC: the NetBIOS computer name, which Windows advertises over
    /// Bluetooth (upper case, at most 15 characters).
    /// </summary>
    Task<string> GetPcBluetoothNameAsync(CancellationToken cancellationToken);
}

public enum RadioTurnOnResult
{
    TurnedOn,
    AlreadyOn,

    /// <summary>Windows (or the user) refused radio access; the user has to use Settings instead.</summary>
    AccessDenied,
    NoRadio,
    Failed,
}

/// <summary>
/// Turns the Bluetooth radio on. Call it ONLY from an explicit user action on a control that says so
/// ("Turn on Bluetooth"); DualAudio never changes the radio on its own.
/// </summary>
public interface IBluetoothRadioControl
{
    /// <summary>Never throws except for cancellation.</summary>
    Task<RadioTurnOnResult> TurnOnAsync(CancellationToken cancellationToken);
}

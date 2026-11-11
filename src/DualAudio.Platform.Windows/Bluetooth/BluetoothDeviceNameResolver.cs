using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DualAudio.Platform.Windows.Bluetooth;

public enum DeviceNameSource
{
    /// <summary>Name of the paired Bluetooth device (association endpoint) with the same container — what Settings shows.</summary>
    BluetoothDevice,

    /// <summary>Fallback: the A2DP interface's own Name (unreliable: intermittently "4").</summary>
    Interface,
}

/// <param name="Reason">Why the fallback was used; null when <see cref="Source"/> is BluetoothDevice.</param>
public sealed record ResolvedDeviceName(string Name, DeviceNameSource Source, string? Reason);

/// <summary>
/// The A2DP interface from AudioPlaybackConnection.GetDeviceSelector() intermittently reports Name="4".
/// The parent Bluetooth device is found by container id among paired Bluetooth association endpoints, and its
/// DeviceInformation.Name is used. BluetoothDevice.Name is deliberately NOT used: it can read
/// "Bluetooth aa:bb:cc:dd:ee:ff", which would put a full address into logs and the UI.
/// </summary>
public static class BluetoothDeviceNameResolver
{
    public const string ContainerIdKey = "System.Devices.ContainerId";
    public const string AepContainerIdKey = "System.Devices.Aep.ContainerId";

    /// <summary>Request these on the A2DP watcher so no extra lookup is needed for the container id.</summary>
    public static IReadOnlyList<string> InterfaceProperties { get; } = [ContainerIdKey, AepContainerIdKey];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Never throws except for cancellation by <paramref name="cancellationToken"/>.</summary>
    public static async Task<ResolvedDeviceName> ResolveAsync(DeviceInformation a2dpInterface, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(a2dpInterface);
        var fallback = a2dpInterface.Name ?? string.Empty;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var container = ContainerOf(a2dpInterface);
            if (container is null)
            {
                // Watcher created without the extra properties: read them for this interface.
                var info = await DeviceInformation.CreateFromIdAsync(a2dpInterface.Id, [ContainerIdKey, AepContainerIdKey])
                    .AsTask(timeout.Token).ConfigureAwait(false);
                container = ContainerOf(info);
            }

            if (container is null)
            {
                return new ResolvedDeviceName(fallback, DeviceNameSource.Interface, "interface has no container id");
            }

            var paired = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    [AepContainerIdKey],
                    DeviceInformationKind.AssociationEndpoint)
                .AsTask(timeout.Token).ConfigureAwait(false);

            var name = PickName(container.Value, paired.Select(d => (ContainerOf(d), (string?)d.Name)));
            return name is null
                ? new ResolvedDeviceName(fallback, DeviceNameSource.Interface, "no paired Bluetooth device with the same container")
                : new ResolvedDeviceName(name, DeviceNameSource.BluetoothDevice, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ResolvedDeviceName(fallback, DeviceNameSource.Interface, $"lookup timed out after {Timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return new ResolvedDeviceName(fallback, DeviceNameSource.Interface, $"lookup threw HRESULT=0x{ex.HResult:X8}");
        }
    }

    /// <summary>The non-blank name of the first candidate in <paramref name="container"/>; null if none.</summary>
    internal static string? PickName(Guid container, IEnumerable<(Guid? Container, string? Name)> candidates) =>
        candidates.FirstOrDefault(c => c.Container == container && !string.IsNullOrWhiteSpace(c.Name)).Name;

    private static Guid? ContainerOf(DeviceInformation info)
    {
        foreach (var key in (string[])[ContainerIdKey, AepContainerIdKey])
        {
            if (info.Properties.TryGetValue(key, out var value) && value is Guid id && id != Guid.Empty)
            {
                return id;
            }
        }

        return null;
    }
}

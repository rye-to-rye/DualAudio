using DualAudio.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DualAudio.Platform.Windows.Diagnostics;

/// <summary>
/// Windows build (HKLM\...\Windows NT\CurrentVersion, read-only) and each Bluetooth adapter's name and driver
/// (DeviceInformation properties of the adapter's device node). Never throws: unknown values are null / "?".
/// No addresses are read.
/// </summary>
public sealed class SystemInfoProvider : ISystemInfoProvider
{
    private const string InstanceIdKey = "System.Devices.DeviceInstanceId";

    // DEVPKEY_Device_DriverDate / DriverVersion / DriverProvider ({a8b865dd-2e3d-4094-ad97-e593a70c75d6}, 2 / 3 / 9).
    private const string DriverDateKey = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 2";
    private const string DriverVersionKey = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 3";
    private const string DriverProviderKey = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 9";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger;

    public SystemInfoProvider(ILogger<SystemInfoProvider>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<SystemInfo> GetAsync(CancellationToken cancellationToken)
    {
        return new SystemInfo(ReadWindowsVersion(), await ReadAdaptersAsync(cancellationToken).ConfigureAwait(false));
    }

    private string ReadWindowsVersion()
    {
        var version = Environment.OSVersion.Version;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var ubr = key?.GetValue("UBR");
            var display = key?.GetValue("DisplayVersion") as string;
            var build = ubr is int u ? $"{version.Major}.{version.Minor}.{version.Build}.{u}" : version.ToString();
            return display is null ? build : $"{build} ({display})";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the Windows version from the registry failed");
            return version.ToString();
        }
    }

    private async Task<IReadOnlyList<BluetoothAdapterInfo>> ReadAdaptersAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var result = new List<BluetoothAdapterInfo>();
        try
        {
            var interfaces = await DeviceInformation
                .FindAllAsync(BluetoothAdapter.GetDeviceSelector(), [InstanceIdKey])
                .AsTask(timeout.Token)
                .ConfigureAwait(false);

            foreach (var adapterInterface in interfaces)
            {
                result.Add(await ReadAdapterAsync(adapterInterface, timeout.Token).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reading Bluetooth adapter information failed");
        }

        return result;
    }

    private async Task<BluetoothAdapterInfo> ReadAdapterAsync(DeviceInformation adapterInterface, CancellationToken token)
    {
        try
        {
            if (adapterInterface.Properties.TryGetValue(InstanceIdKey, out var value) && value is string instanceId)
            {
                var node = await DeviceInformation
                    .CreateFromIdAsync(instanceId, [DriverDateKey, DriverVersionKey, DriverProviderKey], DeviceInformationKind.Device)
                    .AsTask(token)
                    .ConfigureAwait(false);
                return new BluetoothAdapterInfo(
                    node.Name ?? adapterInterface.Name ?? "?",
                    Read(node, DriverProviderKey),
                    Read(node, DriverVersionKey),
                    Read(node, DriverDateKey));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reading the adapter's driver properties failed");
        }

        return new BluetoothAdapterInfo(adapterInterface.Name ?? "?", null, null, null);
    }

    private static string? Read(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out var value) && value is not null
            ? value is DateTimeOffset date ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : value.ToString()
            : null;
}

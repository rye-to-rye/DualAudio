using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;
using Windows.Media.Audio;

namespace DualAudio.Probe;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var log = ProbeLog.CreateNextToExe();
        try
        {
            await using var probe = new Probe(log);
            return await probe.RunAsync();
        }
        catch (Exception ex)
        {
            // Last line of defence: the probe should never crash, but if it does, record why.
            log.Exception("Probe (unhandled)", ex);
            return 99;
        }
    }
}

/// <summary>
/// Interactive probe for Windows.Media.Audio.AudioPlaybackConnection (Bluetooth A2DP sink).
/// </summary>
internal sealed class Probe : IAsyncDisposable
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const int MinBuild = 19041;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private readonly ProbeLog _log;
    private readonly CancellationTokenSource _quit = new();

    // Devices reported by the watcher, in "Added" order. Guarded by _devicesLock.
    private readonly object _devicesLock = new();
    private readonly List<DeviceInformation> _devices = [];

    // The single active connection. Guarded by _connLock (async lock).
    private readonly SemaphoreSlim _connLock = new(1, 1);
    private AudioPlaybackConnection? _connection;
    private string? _connectedId;
    private string? _connectedName;

    // In-flight connect attempt, so a Removed event can abort it. Guarded by _pendingLock.
    private readonly object _pendingLock = new();
    private string? _pendingId;
    private CancellationTokenSource? _pendingRemovedCts;

    private readonly List<Radio> _btRadios = [];
    private DeviceWatcher? _watcher;
    private bool _disposed;

    public Probe(ProbeLog log) => _log = log;

    public async Task<int> RunAsync()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        _log.Info($"DualAudio.Probe starting. Log file: {_log.FilePath}");

        if (!CheckOs()) return 2;
        await ReportRadiosAsync();
        if (!StartWatcher()) return 3;

        PrintHelp();
        await CommandLoopAsync();
        return 0;
    }

    // ---------------------------------------------------------------- 1. OS check

    private bool CheckOs()
    {
        var v = Environment.OSVersion.Version;
        _log.Info($"OS: {RuntimeInformation.OSDescription} | version {v} | build {v.Build} | " +
                  $"arch {RuntimeInformation.OSArchitecture} | .NET {Environment.Version}");

        if (v.Major < 10 || (v.Major == 10 && v.Build < MinBuild))
        {
            _log.Error($"Windows build {v.Build} < {MinBuild} (Windows 10 2004). AudioPlaybackConnection is not available. Exiting.");
            return false;
        }

        bool present;
        try
        {
            present = ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection");
        }
        catch (Exception ex)
        {
            _log.Exception("ApiInformation.IsTypePresent", ex);
            present = false;
        }

        if (!present)
        {
            _log.Error("Windows.Media.Audio.AudioPlaybackConnection is not present on this OS. Exiting.");
            return false;
        }

        _log.Ok($"Build {v.Build} >= {MinBuild} and AudioPlaybackConnection is present.");
        return true;
    }

    // ---------------------------------------------------------------- 2. Radio check

    private async Task ReportRadiosAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _log.Info($"Radio.GetRadiosAsync returned {radios.Count} radio(s): " +
                      string.Join(", ", radios.Select(r => $"{r.Kind}:{r.State}")));

            foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
            {
                _btRadios.Add(radio);
                radio.StateChanged += OnRadioStateChanged;
                if (radio.State == RadioState.On)
                    _log.Ok($"Bluetooth radio \"{radio.Name}\" is On.");
                else
                    _log.Warn($"Bluetooth radio \"{radio.Name}\" is {radio.State}. Turn Bluetooth on in Settings > Bluetooth & devices.");
            }

            if (_btRadios.Count == 0)
                _log.Warn("No Bluetooth radio found (missing adapter, driver, or disabled in Device Manager). Continuing anyway.");
        }
        catch (Exception ex)
        {
            _log.Exception("Radio.GetRadiosAsync", ex);
            _log.Warn("Could not query radios. Continuing anyway.");
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        try { _log.Warn($"Bluetooth radio \"{sender.Name}\" state changed -> {sender.State}"); }
        catch (Exception ex) { _log.Exception("Radio.StateChanged handler", ex); }
    }

    // ---------------------------------------------------------------- 3. Device watcher

    private bool StartWatcher()
    {
        string selector;
        try
        {
            selector = AudioPlaybackConnection.GetDeviceSelector();
            _log.Info($"AudioPlaybackConnection.GetDeviceSelector() = {selector}");
        }
        catch (Exception ex)
        {
            _log.Exception("AudioPlaybackConnection.GetDeviceSelector", ex);
            return false;
        }

        try
        {
            try
            {
                _watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey]);
            }
            catch (Exception ex)
            {
                _log.Exception($"CreateWatcher with property {IsConnectedKey}", ex);
                _log.Warn("Retrying without additional properties (IsConnected will show n/a).");
                _watcher = DeviceInformation.CreateWatcher(selector);
            }

            // Subscribe BEFORE Start().
            _watcher.Added += OnDeviceAdded;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnWatcherStopped;

            _watcher.Start();
            _log.Info("DeviceWatcher started.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Exception("DeviceWatcher create/start", ex);
            return false;
        }
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            string text;
            lock (_devicesLock)
            {
                _devices.Add(info);
                text = Describe(_devices.Count - 1, info);
            }
            _log.Info($"Watcher Added:   {text}");
        }
        catch (Exception ex) { _log.Exception("Watcher.Added handler", ex); }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            string? text = null;
            lock (_devicesLock)
            {
                var index = _devices.FindIndex(d => d.Id == update.Id);
                if (index >= 0)
                {
                    _devices[index].Update(update);
                    text = Describe(index, _devices[index]);
                }
            }
            _log.Info(text is null
                ? $"Watcher Updated: unknown device Id={Redact.ShortId(update.Id)}"
                : $"Watcher Updated: {text}");
        }
        catch (Exception ex) { _log.Exception("Watcher.Updated handler", ex); }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            string? name = null;
            lock (_devicesLock)
            {
                var index = _devices.FindIndex(d => d.Id == update.Id);
                if (index >= 0)
                {
                    name = _devices[index].Name;
                    _devices.RemoveAt(index);
                }
            }
            _log.Warn($"Watcher Removed: \"{name ?? "?"}\" Id={Redact.ShortId(update.Id)} (indexes after it shift down; run 'list')");
            _ = HandleDeviceRemovedAsync(update.Id);
        }
        catch (Exception ex) { _log.Exception("Watcher.Removed handler", ex); }
    }

    private async Task HandleDeviceRemovedAsync(string id)
    {
        try
        {
            // Abort an in-flight connect to this device.
            lock (_pendingLock)
            {
                if (_pendingId == id && _pendingRemovedCts is not null)
                {
                    _log.Warn("Device removed while connecting - aborting connect.");
                    _pendingRemovedCts.Cancel();
                }
            }

            await _connLock.WaitAsync();
            try
            {
                if (_connection is not null && _connectedId == id)
                {
                    _log.Warn($"The connected device \"{_connectedName}\" was removed - disposing connection.");
                    ReleaseConnection_NoLock("device removed");
                }
            }
            finally { _connLock.Release(); }
        }
        catch (Exception ex) { _log.Exception("Device-removed handling", ex); }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        int count;
        lock (_devicesLock) count = _devices.Count;
        _log.Info($"Watcher EnumerationCompleted: {count} device(s).");
        if (count == 0)
            _log.Warn("No A2DP-source devices found. Pair the phone in Settings > Bluetooth & devices (ms-settings:bluetooth) and keep Bluetooth on.");
    }

    private void OnWatcherStopped(DeviceWatcher sender, object args)
    {
        try { _log.Warn($"Watcher Stopped (Status={sender.Status})."); }
        catch (Exception ex) { _log.Exception("Watcher.Stopped handler", ex); }
    }

    private static string Describe(int index, DeviceInformation info)
    {
        var connected = info.Properties.TryGetValue(IsConnectedKey, out var value) && value is not null
            ? value.ToString()
            : "n/a";
        return $"[{index}] Name=\"{info.Name}\" Id={Redact.ShortId(info.Id)} IsEnabled={info.IsEnabled} IsConnected={connected}";
    }

    // ---------------------------------------------------------------- 4. Commands

    private void PrintHelp() => Console.WriteLine("""

        Commands:
          list               show devices that can stream to this PC
          connect <index>    open an A2DP sink connection to that device
          disconnect         close and dispose the current connection
          state              show connection / watcher / radio state
          quit               dispose everything and exit (Ctrl+C also works)

        """);

    private async Task CommandLoopAsync()
    {
        while (!_quit.IsCancellationRequested)
        {
            string? line;
            try
            {
                // ReadLine on a pool thread so Ctrl+C (via _quit) can end the loop.
                line = await Task.Run(Console.ReadLine).WaitAsync(_quit.Token);
            }
            catch (OperationCanceledException) { break; }

            if (line is null) break; // EOF
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            _log.Info($"> {string.Join(' ', parts)}");
            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "list": List(); break;
                    case "connect":
                        if (parts.Length < 2 || !int.TryParse(parts[1], out var index))
                            _log.Warn("Usage: connect <index>   (see 'list')");
                        else
                            await ConnectAsync(index);
                        break;
                    case "disconnect": await DisconnectAsync(); break;
                    case "state": await StateAsync(); break;
                    case "quit" or "exit" or "q": return;
                    case "help" or "?": PrintHelp(); break;
                    default: _log.Warn($"Unknown command '{parts[0]}'. Type 'help'."); break;
                }
            }
            catch (OperationCanceledException) when (_quit.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Exception($"Command '{parts[0]}'", ex); }
        }
    }

    private void List()
    {
        List<string> rows;
        lock (_devicesLock) rows = _devices.Select((d, i) => Describe(i, d)).ToList();

        if (rows.Count == 0)
            _log.Info("No devices. Pair the phone in Windows Settings, make sure Bluetooth is on.");
        foreach (var row in rows) _log.Info(row);
    }

    // ---------------------------------------------------------------- 5. Connect

    private async Task ConnectAsync(int index)
    {
        DeviceInformation? info;
        lock (_devicesLock) info = index >= 0 && index < _devices.Count ? _devices[index] : null;
        if (info is null)
        {
            _log.Warn($"No device at index {index}. Run 'list'.");
            return;
        }

        await _connLock.WaitAsync(_quit.Token);
        using var removedCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_quit.Token, removedCts.Token);
        var step = "TryCreateFromId";
        var ok = false;
        try
        {
            if (_connection is not null)
            {
                _log.Warn($"Already connected to \"{_connectedName}\". Run 'disconnect' first.");
                ok = true; // don't tear down the existing connection
                return;
            }

            _log.Info($"TryCreateFromId(\"{info.Name}\", Id={Redact.ShortId(info.Id)}) ...");
            var conn = AudioPlaybackConnection.TryCreateFromId(info.Id);
            if (conn is null)
            {
                _log.Error("TryCreateFromId returned null: device does not support A2DP source / not available.");
                ok = true; // nothing to release
                return;
            }

            _connection = conn;
            _connectedId = info.Id;
            _connectedName = info.Name;
            conn.StateChanged += OnConnectionStateChanged;
            _log.Info($"Connection object created. State={conn.State}");

            lock (_pendingLock)
            {
                _pendingId = info.Id;
                _pendingRemovedCts = removedCts;
            }

            step = "StartAsync";
            _log.Info("StartAsync() ...");
            linked.CancelAfter(OpenTimeout);
            await conn.StartAsync().AsTask(linked.Token).WaitAsync(linked.Token);
            _log.Info($"StartAsync completed. State={conn.State}");

            step = "OpenAsync";
            _log.Info($"OpenAsync() with {OpenTimeout.TotalSeconds:0} s timeout ...");
            linked.CancelAfter(OpenTimeout); // restart the timer for OpenAsync
            var result = await conn.OpenAsync().AsTask(linked.Token).WaitAsync(linked.Token);

            var extended = FormatExtendedError(result.ExtendedError);
            if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
            {
                ok = true;
                _log.Ok($"OpenAsync: Status={result.Status}, ExtendedError={extended}, State={conn.State}. " +
                        "Play audio on the phone now.");
            }
            else
            {
                _log.Error($"OpenAsync: Status={result.Status}, ExtendedError={extended}, State={conn.State}");
            }
        }
        catch (OperationCanceledException)
        {
            var why = _quit.IsCancellationRequested ? "quit requested"
                : removedCts.IsCancellationRequested ? "device removed"
                : $"timed out after {OpenTimeout.TotalSeconds:0} s";
            _log.Error($"{step} cancelled: {why}.");
        }
        catch (Exception ex)
        {
            _log.Exception(step, ex);
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingId = null;
                _pendingRemovedCts = null;
            }
            if (!ok) ReleaseConnection_NoLock("connect failed");
            _connLock.Release();
        }
    }

    private static string FormatExtendedError(Exception? ex) =>
        ex is null || ex.HResult == 0
            ? "none"
            : $"HRESULT=0x{ex.HResult:X8} ({ProbeLog.OneLine(ex.Message)})";

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        try { _log.Warn($"Connection StateChanged -> {sender.State}"); }
        catch (Exception ex) { _log.Exception("Connection.StateChanged handler", ex); }
    }

    // ---------------------------------------------------------------- 6. Disconnect / state / dispose

    private async Task DisconnectAsync()
    {
        await _connLock.WaitAsync();
        try
        {
            if (_connection is null) _log.Info("Not connected.");
            else ReleaseConnection_NoLock("user disconnect");
        }
        finally { _connLock.Release(); }
    }

    /// <summary>Unsubscribe, Dispose, null. Caller must hold _connLock.</summary>
    private void ReleaseConnection_NoLock(string reason)
    {
        var conn = _connection;
        if (conn is null) return;

        var name = _connectedName;
        _connection = null;
        _connectedId = null;
        _connectedName = null;

        try { conn.StateChanged -= OnConnectionStateChanged; }
        catch (Exception ex) { _log.Exception("Unsubscribe StateChanged", ex); }

        try { conn.Dispose(); }
        catch (Exception ex) { _log.Exception("AudioPlaybackConnection.Dispose", ex); }

        _log.Info($"Connection to \"{name}\" disposed ({reason}).");
    }

    private async Task StateAsync()
    {
        await _connLock.WaitAsync();
        try
        {
            if (_connection is null)
                _log.Info("Connection: none");
            else
            {
                try { _log.Info($"Connection: \"{_connectedName}\" Id={Redact.ShortId(_connectedId)} State={_connection.State}"); }
                catch (Exception ex) { _log.Exception("Read connection state", ex); }
            }
        }
        finally { _connLock.Release(); }

        try { _log.Info($"Watcher: {_watcher?.Status.ToString() ?? "none"}"); }
        catch (Exception ex) { _log.Exception("Read watcher status", ex); }

        foreach (var radio in _btRadios)
        {
            try { _log.Info($"Bluetooth radio \"{radio.Name}\": {radio.State}"); }
            catch (Exception ex) { _log.Exception("Read radio state", ex); }
        }
        if (_btRadios.Count == 0) _log.Info("Bluetooth radio: none found at startup");
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true; // shut down cleanly instead of killing the process
        _log.Warn("Ctrl+C - shutting down.");
        try { _quit.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Info("Shutting down ...");

        Console.CancelKeyPress -= OnCancelKeyPress;
        _quit.Cancel();

        await _connLock.WaitAsync();
        try { ReleaseConnection_NoLock("quit"); }
        finally { _connLock.Release(); }

        if (_watcher is not null)
        {
            try
            {
                _watcher.Added -= OnDeviceAdded;
                _watcher.Removed -= OnDeviceRemoved;
                _watcher.Updated -= OnDeviceUpdated;
                _watcher.EnumerationCompleted -= OnEnumerationCompleted;
                _watcher.Stopped -= OnWatcherStopped;
                if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    _watcher.Stop();
            }
            catch (Exception ex) { _log.Exception("DeviceWatcher stop", ex); }
            _watcher = null;
        }

        foreach (var radio in _btRadios)
        {
            try { radio.StateChanged -= OnRadioStateChanged; }
            catch (Exception ex) { _log.Exception("Unsubscribe Radio.StateChanged", ex); }
        }
        _btRadios.Clear();

        _quit.Dispose();
        _log.Info("Shutdown complete.");
    }
}

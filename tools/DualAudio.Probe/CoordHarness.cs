using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Errors;
using DualAudio.Platform.Windows;
using DualAudio.Platform.Windows.Bluetooth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DualAudio.Probe;

/// <summary>
/// `DualAudio.Probe coord [phone-name]`: ConnectionCoordinator wired to the real Platform.Windows adapters
/// through AddWindowsPlatform, to test connect / Bluetooth off+on / out-of-range reconnect without UI.
/// </summary>
internal sealed class CoordHarness
{
    private readonly ProbeLog _log;
    private readonly ConnectionCoordinator _coordinator;
    private readonly IRadioMonitor _radio;
    private readonly IBluetoothAudioSourceWatcher _watcher;
    private readonly BluetoothSettingsLauncher _settings;
    private readonly CancellationTokenSource _quit = new();

    private CoordHarness(ProbeLog log, IServiceProvider services)
    {
        _log = log;
        _radio = services.GetRequiredService<IRadioMonitor>();
        _watcher = services.GetRequiredService<IBluetoothAudioSourceWatcher>();
        _settings = services.GetRequiredService<BluetoothSettingsLauncher>();
        _coordinator = services.GetRequiredService<ConnectionCoordinator>();
    }

    public static async Task<int> RunAsync(ProbeLog log, string[] args)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().AddProvider(new ProbeLoggerProvider(log)).SetMinimumLevel(LogLevel.Debug))
            .AddWindowsPlatform()
            .AddSingleton(new ConnectionCoordinatorOptions())
            .AddSingleton(sp => new ConnectionCoordinator(
                sp.GetRequiredService<IBluetoothAudioSourceWatcher>(),
                sp.GetRequiredService<IAudioReceiver>(),
                sp.GetRequiredService<IRadioMonitor>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ConnectionCoordinatorOptions>(),
                sp.GetRequiredService<ILogger<ConnectionCoordinator>>()));

        // Disposal order (reverse of creation): coordinator first, then the adapters.
        await using var provider = services.BuildServiceProvider();
        var harness = new CoordHarness(log, provider);
        return await harness.RunCoreAsync(args.Length > 0 ? string.Join(' ', args) : null);
    }

    private async Task<int> RunCoreAsync(string? autoConnectName)
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            _log.Info("coord harness: ConnectionCoordinator + real adapters. Type 'help'.");
            _coordinator.StateChanged += OnStateChanged;
            _radio.StatusChanged += OnRadioStatusChanged;

            // The coordinator subscribed in its constructor; start the sources only now.
            var enumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _watcher.EnumerationCompleted += (_, _) => enumerated.TrySetResult();
            await _radio.StartAsync(_quit.Token);
            _log.Info($"Radio status: {_radio.Status}");
            _watcher.Start();
            await Task.WhenAny(enumerated.Task, Task.Delay(TimeSpan.FromSeconds(10), _quit.Token));

            List();
            if (autoConnectName is not null)
            {
                var phone = Ordered().FirstOrDefault(d => d.Name.Contains(autoConnectName, StringComparison.OrdinalIgnoreCase));
                if (phone is null)
                {
                    _log.Warn($"No device name contains \"{autoConnectName}\".");
                }
                else
                {
                    await _coordinator.ConnectAsync(phone.Id, _quit.Token);
                }
            }

            PrintHelp();
            await CommandLoopAsync();
            return 0;
        }
        catch (OperationCanceledException) when (_quit.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _coordinator.StateChanged -= OnStateChanged;
            _radio.StatusChanged -= OnRadioStatusChanged;
            _log.Info("coord harness shutting down (coordinator, then adapters, are disposed).");
            await _coordinator.DisposeAsync();
        }
    }

    private static void PrintHelp() => Console.WriteLine("""

        coord commands:
          list               devices known to the coordinator (names from watcher Added)
          connect <index>    ConnectionCoordinator.ConnectAsync
          disconnect         ConnectionCoordinator.DisconnectAsync
          state              coordinator state, error, reconnect schedule, radio, receiver
          auto on|off        AutoReconnect
          settings           open ms-settings:bluetooth
          quit               dispose everything and exit (Ctrl+C also works)

        Acceptance checks: connect + play audio; PC Bluetooth off -> Error(BluetoothOff);
        Bluetooth on -> reconnect within 30 s; walk out of range and back -> reconnects.

        """);

    private async Task CommandLoopAsync()
    {
        while (!_quit.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Task.Run(Console.ReadLine).WaitAsync(_quit.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            _log.Info($"> {string.Join(' ', parts)}");
            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "list":
                        List();
                        break;
                    case "connect":
                        var devices = Ordered();
                        if (parts.Length < 2 || !int.TryParse(parts[1], out var index) || index < 0 || index >= devices.Count)
                        {
                            _log.Warn("Usage: connect <index>   (see 'list')");
                        }
                        else
                        {
                            await _coordinator.ConnectAsync(devices[index].Id, _quit.Token);
                        }

                        break;
                    case "disconnect":
                        await _coordinator.DisconnectAsync();
                        break;
                    case "state":
                        State();
                        break;
                    case "auto":
                        if (parts.Length < 2 || parts[1] is not ("on" or "off"))
                        {
                            _log.Warn("Usage: auto on|off");
                        }
                        else
                        {
                            _coordinator.AutoReconnect = parts[1] == "on";
                            _log.Info($"AutoReconnect = {_coordinator.AutoReconnect}");
                        }

                        break;
                    case "settings":
                        _log.Info($"Open Bluetooth settings: {await _settings.OpenAsync(_quit.Token)}");
                        break;
                    case "help" or "?":
                        PrintHelp();
                        break;
                    case "quit" or "exit" or "q":
                        return;
                    default:
                        _log.Warn($"Unknown command '{parts[0]}'. Type 'help'.");
                        break;
                }
            }
            catch (OperationCanceledException) when (_quit.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Exception($"Command '{parts[0]}'", ex);
            }
        }
    }

    private List<PhoneDevice> Ordered() =>
        _coordinator.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

    private void List()
    {
        var devices = Ordered();
        if (devices.Count == 0)
        {
            _log.Warn("No devices. Pair the phone in Settings > Bluetooth & devices ('settings') and keep Bluetooth on.");
        }

        for (var i = 0; i < devices.Count; i++)
        {
            _log.Info($"[{i}] \"{devices[i].Name}\" {LogRedaction.DeviceId(devices[i].Id)}");
        }
    }

    private void State()
    {
        var c = _coordinator;
        _log.Info($"Coordinator: {c.CurrentState}" +
                  (c.LastError is { } e ? $" error={e}" : string.Empty) +
                  $" device=\"{c.ActiveDevice?.Name ?? "-"}\" auto={c.AutoReconnect}" +
                  (c.IsReconnecting ? $" reconnecting, next at {c.NextReconnectAt?.ToLocalTime():HH:mm:ss}" : string.Empty));
        _log.Info($"Radio: {_radio.Status}; watcher enumeration completed: {_watcher.IsEnumerationCompleted}");
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var line = $"STATE {e.Previous} -> {e.Current}" + (e.Device is { } d ? $" (\"{d.Name}\")" : string.Empty);
        if (e.Error is { } kind)
        {
            var message = ErrorMessages.For(kind);
            line += $" error={kind}: {message.Title}. {message.Action}";
        }

        if (e.Current == ConnectionState.Connected)
        {
            _log.Ok(line + " - play audio on the phone now.");
        }
        else if (e.Current == ConnectionState.Error)
        {
            _log.Error(line);
        }
        else
        {
            _log.Warn(line);
        }
    }

    private void OnRadioStatusChanged(object? sender, RadioStatusChangedEventArgs e) => _log.Warn($"RADIO -> {e.Status}");

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _log.Warn("Ctrl+C - shutting down.");
        try
        {
            _quit.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualAudio.App.Services;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.ViewModels;

/// <summary>
/// The main window. Every service event is marshalled to the UI thread here, via <see cref="IUiDispatcher"/>, and
/// nowhere else; handlers then re-read the services' current state, so a burst of events just converges.
/// Every service call runs on the thread pool (never on the UI thread) and never throws into the UI.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    public const string PhoneAudioUnavailableToolTip =
        "No phone audio yet. Connect your phone and play something on it to change its volume.";

    private readonly ConnectionCoordinator _coordinator;
    private readonly PhoneAudioService _phoneAudio;
    private readonly IBluetoothAudioSourceWatcher _watcher;
    private readonly IOutputDeviceService _outputs;
    private readonly ISettingsStore _settings;
    private readonly ISystemSettingsLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;

    // UI-thread state.
    private string? _preferredDeviceId;
    private bool _applyingServiceState;
    private int? _pendingVolume;
    private Task _volumePush = Task.CompletedTask;
    private int _mutePushesInFlight;
    private bool _disposed;

    public DashboardViewModel(
        ConnectionCoordinator coordinator,
        PhoneAudioService phoneAudio,
        IBluetoothAudioSourceWatcher watcher,
        IOutputDeviceService outputs,
        ISettingsStore settings,
        ISystemSettingsLauncher launcher,
        IUiDispatcher dispatcher,
        ILogger<DashboardViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _phoneAudio = phoneAudio;
        _watcher = watcher;
        _outputs = outputs;
        _settings = settings;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        _coordinator.StateChanged += OnConnectionStateChanged;
        _phoneAudio.Changed += OnPhoneAudioChanged;
        _watcher.DeviceAdded += OnDevicesChanged;
        _watcher.DeviceRemoved += OnDevicesChanged;
        _watcher.EnumerationCompleted += OnDevicesChanged;
        _outputs.DefaultEndpointChanged += OnDefaultEndpointChanged;

        ApplyConnectionState();
        ApplyPhoneAudio(_phoneAudio.Current);
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DeviceItem? selectedDevice;

    [ObservableProperty]
    private ConnectionState connectionState;

    [ObservableProperty]
    private StatusKind statusKind;

    [ObservableProperty]
    private string statusText = "Disconnected";

    /// <summary>Enabling or Opening (incl. the BUG-001 priming cycle): Connect/Disconnect are disabled.</summary>
    [ObservableProperty]
    private bool isTransitioning;

    /// <summary>A link is wanted (connecting, connected, or reconnecting): show Disconnect instead of Connect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectVisible))]
    private bool isDisconnectVisible;

    public bool IsConnectVisible => !IsDisconnectVisible;

    [ObservableProperty]
    private int phoneVolume = 100;

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhoneAudioToolTip))]
    private bool isPhoneAudioAvailable;

    public string? PhoneAudioToolTip => IsPhoneAudioAvailable ? null : PhoneAudioUnavailableToolTip;

    [ObservableProperty]
    private bool isReceivingAudio;

    [ObservableProperty]
    private string outputDeviceName = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsErrorVisible), nameof(HasErrorAction))]
    [NotifyCanExecuteChangedFor(nameof(ErrorActionCommand))]
    private ErrorBanner? error;

    public bool IsErrorVisible => Error is not null;

    public bool HasErrorAction => Error is { Action: not ErrorAction.None };

    /// <summary>Test hook: the latest volume push (coalesced while the slider is dragged).</summary>
    internal Task VolumePush => _volumePush;

    /// <summary>Test hook: the latest mute push.</summary>
    internal Task MutePush { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the remembered phone and the current output device. Call once, on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var settings = await Task.Run(() => _settings.LoadAsync(CancellationToken.None));
            _preferredDeviceId = settings.PreferredDeviceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings for the dashboard failed");
        }

        // At start the remembered phone wins over a selection made automatically before settings were read.
        if (_preferredDeviceId is not null && Devices.Any(d => string.Equals(d.Id, _preferredDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedDevice = null;
        }

        RefreshDevices();
        ApplyConnectionState();
        ApplyPhoneAudio(_phoneAudio.Current);

        try
        {
            var endpoint = await Task.Run(() => _outputs.GetDefaultEndpointAsync(CancellationToken.None));
            OutputDeviceName = DescribeEndpoint(endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the default output device failed");
            OutputDeviceName = "Unknown";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnConnectionStateChanged;
        _phoneAudio.Changed -= OnPhoneAudioChanged;
        _watcher.DeviceAdded -= OnDevicesChanged;
        _watcher.DeviceRemoved -= OnDevicesChanged;
        _watcher.EnumerationCompleted -= OnDevicesChanged;
        _outputs.DefaultEndpointChanged -= OnDefaultEndpointChanged;
    }

    // ---------------------------------------------------------------- commands

    private bool CanConnect() => SelectedDevice is not null && !IsDisconnectVisible && !IsTransitioning;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        await RememberDeviceAsync(device.Id);
        await ConnectToAsync(device.Id);
    }

    private bool CanDisconnect() => IsDisconnectVisible && !IsTransitioning;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => RunSafeAsync(() => _coordinator.DisconnectAsync(), "disconnect");

    private bool CanRunErrorAction() => HasErrorAction;

    [RelayCommand(CanExecute = nameof(CanRunErrorAction))]
    private async Task ErrorActionAsync()
    {
        switch (Error?.Action)
        {
            case ErrorAction.OpenBluetoothSettings:
                await RunSafeAsync(_launcher.OpenBluetoothSettingsAsync, "open Bluetooth settings");
                break;
            case ErrorAction.Retry:
                var id = _coordinator.ActiveDevice?.Id ?? SelectedDevice?.Id;
                if (id is not null)
                {
                    await ConnectToAsync(id);
                }

                break;
        }
    }

    /// <summary>"Add a phone": pairing is always done by the user in Settings.</summary>
    [RelayCommand]
    private Task AddPhoneAsync() => RunSafeAsync(_launcher.OpenBluetoothSettingsAsync, "open Bluetooth settings");

    [RelayCommand]
    private Task OpenPerAppOutputSettingsAsync() => RunSafeAsync(_launcher.OpenAppVolumeSettingsAsync, "open app volume settings");

    private Task ConnectToAsync(string deviceId)
    {
        _logger.LogInformation("User asked to connect to {Device}", LogRedaction.DeviceId(deviceId));
        return RunSafeAsync(() => _coordinator.ConnectAsync(deviceId), "connect");
    }

    private async Task RememberDeviceAsync(string deviceId)
    {
        _preferredDeviceId = deviceId;
        await RunSafeAsync(
            async () =>
            {
                var current = await _settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (current.PreferredDeviceId != deviceId)
                {
                    await _settings.SaveAsync(current with { PreferredDeviceId = deviceId }, CancellationToken.None).ConfigureAwait(false);
                }
            },
            "save preferred phone");
    }

    // ---------------------------------------------------------------- volume / mute (UI -> service)

    partial void OnPhoneVolumeChanged(int value)
    {
        if (_applyingServiceState)
        {
            return;
        }

        // Coalesce slider drags: one push in flight, then the newest value.
        var startLoop = _pendingVolume is null && _volumePush.IsCompleted;
        _pendingVolume = Math.Clamp(value, 0, 100);
        if (startLoop)
        {
            _volumePush = PushVolumeAsync();
        }
    }

    private async Task PushVolumeAsync()
    {
        while (_pendingVolume is { } volume)
        {
            await RunSafeAsync(() => _phoneAudio.SetVolumeAsync(volume), "set phone volume");
            if (_pendingVolume == volume)
            {
                _pendingVolume = null;
            }
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        UpdateStatus();
        if (_applyingServiceState)
        {
            return;
        }

        MutePush = PushMuteAsync(value);
    }

    private async Task PushMuteAsync(bool muted)
    {
        _mutePushesInFlight++;
        try
        {
            await RunSafeAsync(() => _phoneAudio.SetMutedAsync(muted), muted ? "mute phone" : "unmute phone");
        }
        finally
        {
            _mutePushesInFlight--;
        }
    }

    // ---------------------------------------------------------------- service events (any thread -> UI)

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) => _dispatcher.Post(ApplyConnectionState);

    private void OnPhoneAudioChanged(object? sender, PhoneAudioChangedEventArgs e) => _dispatcher.Post(() => ApplyPhoneAudio(_phoneAudio.Current));

    private void OnDevicesChanged(object? sender, EventArgs e) => _dispatcher.Post(RefreshDevices);

    private void OnDefaultEndpointChanged(object? sender, DefaultEndpointChangedEventArgs e)
    {
        var name = DescribeEndpoint(e.Endpoint);
        _dispatcher.Post(() => OutputDeviceName = name);
    }

    // ---------------------------------------------------------------- state -> properties (UI thread)

    private void ApplyConnectionState()
    {
        if (_disposed)
        {
            return;
        }

        var state = _coordinator.CurrentState;
        var reconnecting = _coordinator.IsReconnecting;

        ConnectionState = state;
        IsTransitioning = state is ConnectionState.Enabling or ConnectionState.Opening;
        IsDisconnectVisible = state is ConnectionState.Enabling or ConnectionState.WaitingForPhone or ConnectionState.Opening or ConnectionState.Connected
                              || (state == ConnectionState.Disconnected && reconnecting);
        Error = ErrorBanner.From(state, _coordinator.LastError, reconnecting);

        if (_coordinator.ActiveDevice is { } active && Devices.FirstOrDefault(d => d.Id == active.Id) is { } item)
        {
            SelectedDevice = item;
        }

        UpdateStatus();
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void ApplyPhoneAudio(PhoneAudioSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        _applyingServiceState = true;
        try
        {
            // Don't fight the user: while their own change is still being applied, keep what they set.
            if (_pendingVolume is null && _volumePush.IsCompleted)
            {
                PhoneVolume = snapshot.VolumePercent;
            }

            if (_mutePushesInFlight == 0)
            {
                IsMuted = snapshot.Muted;
            }
        }
        finally
        {
            _applyingServiceState = false;
        }

        IsPhoneAudioAvailable = snapshot.IsAvailable;
        IsReceivingAudio = snapshot.IsReceivingAudio;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var status = DashboardStatus.From(ConnectionState, _coordinator.IsReconnecting, IsReceivingAudio, IsMuted);
        StatusKind = status.Kind;
        StatusText = status.Text;
    }

    private void RefreshDevices()
    {
        if (_disposed)
        {
            return;
        }

        var selectedId = SelectedDevice?.Id;
        var latest = _coordinator.Devices.Select(DeviceItem.From).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        Devices.Clear();
        foreach (var item in latest)
        {
            Devices.Add(item);
        }

        SelectedDevice = Find(selectedId) ?? Find(_coordinator.ActiveDevice?.Id) ?? Find(_preferredDeviceId) ?? Devices.FirstOrDefault();

        DeviceItem? Find(string? id) => id is null ? null : Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeEndpoint(AudioEndpoint? endpoint) => endpoint?.Name ?? "No output device";

    /// <summary>Runs a service call on the thread pool; failures are logged, never thrown into the UI.</summary>
    private async Task RunSafeAsync(Func<Task> call, string what)
    {
        try
        {
            await Task.Run(call);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard action failed: {What}", what);
        }
    }
}

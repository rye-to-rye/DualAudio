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
    private IReadOnlyDictionary<string, string> _nicknames = new Dictionary<string, string>();
    private bool _applyingServiceState;
    private int? _pendingVolume;
    private Task _volumePush = Task.CompletedTask;
    private int _mutePushesInFlight;
    private bool _initialized;
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
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(ForgetDeviceCommand))]
    private DeviceItem? selectedDevice;

    /// <summary>Mirrors <c>AppSettings.AutoConnectOnStart</c>; changing it is saved right away.</summary>
    [ObservableProperty]
    private bool autoConnectOnStart = true;

    /// <summary>Calm "Phone not found — Retry" panel: the coordinator gave up on its own (no error banner).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFindPhoneCommand))]
    private bool isPhoneNotFound;

    [ObservableProperty]
    private string phoneNotFoundMessage = PhoneNotFound.Message(null);

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

    /// <summary>Test hook: the latest settings write (remember phone, forget, auto-connect toggle).</summary>
    internal Task SettingsPush { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the remembered phone and the current output device. Call once, on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var settings = await Task.Run(() => _settings.LoadAsync(CancellationToken.None));
            _preferredDeviceId = settings.PreferredDeviceId;
            _nicknames = settings.DeviceNicknames;
            _applyingServiceState = true;
            try
            {
                AutoConnectOnStart = settings.AutoConnectOnStart;
            }
            finally
            {
                _applyingServiceState = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings for the dashboard failed");
        }

        // From now on a successful connection is remembered. Not before: while onboarding runs, only onboarding
        // saves the phone, and only once audio actually arrives.
        _initialized = true;

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

        // The phone is remembered once the connection succeeds (see ApplyConnectionState), not on the click.
        await ConnectToAsync(device.Id);
    }

    private bool CanForgetDevice() => SelectedDevice is { IsSaved: true };

    /// <summary>
    /// "Forget in DualAudio": removes the phone (and its nickname) from DualAudio's settings only. It stays paired in
    /// Windows and an active connection is left alone.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForgetDevice))]
    private async Task ForgetDeviceAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        _logger.LogInformation("User asked DualAudio to forget {Device}", LogRedaction.DeviceId(device.Id));
        if (string.Equals(_preferredDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
        {
            _preferredDeviceId = null;
        }

        var nicknames = new Dictionary<string, string>(_nicknames);
        nicknames.Remove(device.Id);
        _nicknames = nicknames;
        RefreshDevices();

        await (SettingsPush = UpdateSettingsAsync(s => s.Forget(device.Id), "forget phone"));
    }

    private bool CanRetryFindPhone() => IsPhoneNotFound;

    /// <summary>"Phone not found — Retry": looks for the phone again, with automatic retries.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFindPhone))]
    private Task RetryFindPhoneAsync()
    {
        var id = _coordinator.ActiveDevice?.Id ?? _preferredDeviceId ?? SelectedDevice?.Id;
        if (id is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("User asked to look for {Device} again", LogRedaction.DeviceId(id));
        return RunSafeAsync(() => _coordinator.AutoConnectAsync(id), "retry finding phone");
    }

    partial void OnAutoConnectOnStartChanged(bool value)
    {
        if (_applyingServiceState)
        {
            return;
        }

        SettingsPush = UpdateSettingsAsync(s => s with { AutoConnectOnStart = value }, "save auto-connect");
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

    private Task RememberDeviceAsync(string deviceId)
    {
        _preferredDeviceId = deviceId;
        RefreshDevices();
        return UpdateSettingsAsync(
            s => string.Equals(s.PreferredDeviceId, deviceId, StringComparison.Ordinal) ? s : s with { PreferredDeviceId = deviceId },
            "save preferred phone");
    }

    /// <summary>Re-reads the settings (other fields are kept), applies <paramref name="change"/>, saves if it changed.</summary>
    private Task UpdateSettingsAsync(Func<Core.Settings.AppSettings, Core.Settings.AppSettings> change, string what) =>
        RunSafeAsync(
            async () =>
            {
                var current = await _settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                var updated = change(current);
                if (!ReferenceEquals(updated, current))
                {
                    await _settings.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
                }
            },
            what);

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
        var gaveUp = _coordinator.GaveUp;
        var wasConnected = ConnectionState == ConnectionState.Connected;

        ConnectionState = state;
        IsTransitioning = state is ConnectionState.Enabling or ConnectionState.Opening;
        IsDisconnectVisible = state is ConnectionState.Enabling or ConnectionState.WaitingForPhone or ConnectionState.Opening or ConnectionState.Connected
                              || (state == ConnectionState.Disconnected && reconnecting);
        Error = ErrorBanner.From(state, _coordinator.LastError, reconnecting, gaveUp);
        IsPhoneNotFound = state == ConnectionState.Error && gaveUp;

        var active = _coordinator.ActiveDevice;
        if (active is not null && Devices.FirstOrDefault(d => d.Id == active.Id) is { } item)
        {
            SelectedDevice = item;
        }

        if (IsPhoneNotFound)
        {
            var name = Devices.FirstOrDefault(d => d.Id == active?.Id)?.Name ?? active?.Name;
            PhoneNotFoundMessage = PhoneNotFound.Message(name);
        }

        // Remembered on success (entering Connected), so a phone that never connects doesn't replace the saved one.
        if (_initialized && !wasConnected && state == ConnectionState.Connected && active is not null
            && !string.Equals(active.Id, _preferredDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            SettingsPush = RememberDeviceAsync(active.Id);
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
        var status = DashboardStatus.From(ConnectionState, _coordinator.IsReconnecting, IsReceivingAudio, IsMuted, _coordinator.GaveUp);
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
        var latest = _coordinator.Devices
            .Select(d => DeviceItem.From(
                d,
                _nicknames.TryGetValue(d.Id, out var nickname) && !string.IsNullOrWhiteSpace(nickname) ? nickname : null,
                string.Equals(d.Id, _preferredDeviceId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.IsSaved) // the saved phone first
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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

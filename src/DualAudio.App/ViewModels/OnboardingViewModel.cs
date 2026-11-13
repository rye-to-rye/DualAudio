using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualAudio.App.Services;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Diagnostics;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.ViewModels;

/// <summary>
/// First-run window: (1) capability checks, (2) pair from the phone, (3) pick the phone + optional nickname,
/// (4) connect and play. Success requires <see cref="PhoneAudioService.IsReceivingAudio"/>, not just Connected: only
/// then is the phone saved (PreferredDeviceId, nickname, OnboardingCompleted).
/// Same threading rules as the dashboard: service events are marshalled via <see cref="IUiDispatcher"/> and the
/// handlers re-read service state; service calls run on the thread pool and never throw into the UI.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    public const int StepCount = 4;

    /// <summary>How long step 4 waits for phone audio before showing help (it keeps waiting afterwards).</summary>
    public static readonly TimeSpan NoAudioHelpDelay = TimeSpan.FromSeconds(60);

    private readonly ConnectionCoordinator _coordinator;
    private readonly PhoneAudioService _phoneAudio;
    private readonly IBluetoothAudioSourceWatcher _watcher;
    private readonly IRadioMonitor _radio;
    private readonly ICapabilityService _capabilities;
    private readonly IBluetoothRadioControl _radioControl;
    private readonly ISettingsStore _settings;
    private readonly ISystemSettingsLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();

    // UI-thread state.
    private int _checkVersion;
    private int _connectVersion;
    private bool _nameLoaded;
    private bool _disposed;

    public OnboardingViewModel(
        ConnectionCoordinator coordinator,
        PhoneAudioService phoneAudio,
        IBluetoothAudioSourceWatcher watcher,
        IRadioMonitor radio,
        ICapabilityService capabilities,
        IBluetoothRadioControl radioControl,
        ISettingsStore settings,
        ISystemSettingsLauncher launcher,
        IUiDispatcher dispatcher,
        IClock clock,
        ILogger<OnboardingViewModel>? logger = null)
    {
        _coordinator = coordinator;
        _phoneAudio = phoneAudio;
        _watcher = watcher;
        _radio = radio;
        _capabilities = capabilities;
        _radioControl = radioControl;
        _settings = settings;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        _coordinator.StateChanged += OnConnectionChanged;
        _phoneAudio.Changed += OnPhoneAudioChanged;
        _watcher.DeviceAdded += OnDevicesChanged;
        _watcher.DeviceRemoved += OnDevicesChanged;
        _watcher.EnumerationCompleted += OnDevicesChanged;
        _radio.StatusChanged += OnRadioChanged;
    }

    /// <summary>Raised on the UI thread when the user finishes or skips.</summary>
    public event EventHandler<OnboardingOutcome>? Finished;

    // ---------------------------------------------------------------- step

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepNumber), nameof(StepTitle), nameof(IsChecksStep), nameof(IsPairStep), nameof(IsPickStep), nameof(IsConnectStep), nameof(IsNextVisible))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private OnboardingStep step;

    public int StepNumber => (int)Step + 1;

    public string StepTitle => Step switch
    {
        OnboardingStep.Checks => "Check this PC",
        OnboardingStep.Pair => "Pair your phone",
        OnboardingStep.PickPhone => "Choose your phone",
        _ => "Play something",
    };

    public bool IsChecksStep => Step == OnboardingStep.Checks;

    public bool IsPairStep => Step == OnboardingStep.Pair;

    public bool IsPickStep => Step == OnboardingStep.PickPhone;

    public bool IsConnectStep => Step == OnboardingStep.Connect;

    public bool IsNextVisible => Step != OnboardingStep.Connect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SkipCommand))]
    private bool canSkip;

    // ---------------------------------------------------------------- step 1: checks

    public ObservableCollection<CheckRow> Checks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private CapabilityReport? report;

    [ObservableProperty]
    private bool isChecking;

    /// <summary>Result of the last "Turn on Bluetooth" click when it didn't work; null otherwise.</summary>
    [ObservableProperty]
    private string? radioMessage;

    // ---------------------------------------------------------------- step 2: pair

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairInstruction), nameof(IPhonePairTip), nameof(AndroidPairTip), nameof(IPhoneNoAudioTip), nameof(AndroidNoAudioTip))]
    private string pcBluetoothName = Environment.MachineName;

    public string PairInstruction => OnboardingText.PairInstruction(PcBluetoothName);

    public string IPhonePairTip => OnboardingText.IPhonePairTip(PcBluetoothName);

    public string AndroidPairTip => OnboardingText.AndroidPairTip(PcBluetoothName);

    // ---------------------------------------------------------------- step 3: pick

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private DeviceItem? selectedDevice;

    [ObservableProperty]
    private bool hasDevices;

    /// <summary>Optional, DualAudio-only (stored in settings; never renames the system device).</summary>
    [ObservableProperty]
    private string nickname = string.Empty;

    // ---------------------------------------------------------------- step 4: connect + play

    [ObservableProperty]
    private string connectStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectErrorVisible))]
    private ErrorMessage? connectError;

    public bool IsConnectErrorVisible => ConnectError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand), nameof(BackCommand), nameof(TryAgainCommand))]
    private bool isSucceeded;

    /// <summary>Shown after <see cref="NoAudioHelpDelay"/> without phone audio.</summary>
    [ObservableProperty]
    private bool isNoAudioHelpVisible;

    [ObservableProperty]
    private bool isMutedHintVisible;

    public string IPhoneNoAudioTip => OnboardingText.IPhoneNoAudioTip(PcBluetoothName);

    public string AndroidNoAudioTip => OnboardingText.AndroidNoAudioTip(PcBluetoothName);

    // ---------------------------------------------------------------- test hooks

    internal Task CheckTask { get; private set; } = Task.CompletedTask;

    internal Task ConnectTask { get; private set; } = Task.CompletedTask;

    internal Task SaveTask { get; private set; } = Task.CompletedTask;

    internal Task NameTask { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Reads settings (Skip) and runs the checks. Call once, on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var settings = await Task.Run(() => _settings.LoadAsync(_lifetime.Token));
            CanSkip = OnboardingPolicy.CanSkip(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings for onboarding failed");
        }

        RefreshDevices();
        await RunChecksAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnConnectionChanged;
        _phoneAudio.Changed -= OnPhoneAudioChanged;
        _watcher.DeviceAdded -= OnDevicesChanged;
        _watcher.DeviceRemoved -= OnDevicesChanged;
        _watcher.EnumerationCompleted -= OnDevicesChanged;
        _radio.StatusChanged -= OnRadioChanged;
        _lifetime.Cancel(); // not disposed: late timer/task continuations may still read the token
    }

    // ---------------------------------------------------------------- navigation

    private bool CanGoNext() => Step switch
    {
        OnboardingStep.Checks => Report is { CanContinue: true },
        OnboardingStep.Pair => true,
        OnboardingStep.PickPhone => SelectedDevice is not null,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case OnboardingStep.Checks:
                Step = OnboardingStep.Pair;
                if (!_nameLoaded)
                {
                    _nameLoaded = true;
                    NameTask = LoadPcNameAsync();
                    await NameTask;
                }

                break;
            case OnboardingStep.Pair:
                Step = OnboardingStep.PickPhone;
                RefreshDevices();
                break;
            case OnboardingStep.PickPhone:
                Step = OnboardingStep.Connect;
                ConnectTask = StartConnectAsync(fresh: false);
                await ConnectTask;
                break;
        }
    }

    private bool CanGoBack() => Step != OnboardingStep.Checks && !IsSucceeded;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (Step == OnboardingStep.Connect)
        {
            _connectVersion++; // stops the help timer; the connection itself is left alone
            IsNoAudioHelpVisible = false;
        }

        Step = Step - 1;
        if (Step == OnboardingStep.Checks)
        {
            CheckTask = RunChecksAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        _logger.LogInformation("Onboarding skipped");
        Finished?.Invoke(this, OnboardingOutcome.Skipped);
    }

    [RelayCommand(CanExecute = nameof(IsSucceeded))]
    private void Finish() => Finished?.Invoke(this, OnboardingOutcome.Completed);

    // ---------------------------------------------------------------- step 1

    [RelayCommand]
    private Task CheckAgainAsync() => CheckTask = RunChecksAsync();

    [RelayCommand]
    private async Task FixAsync(CheckRow? row)
    {
        switch (row?.Fix)
        {
            case CheckFix.OpenBluetoothSettings:
                await OpenBluetoothSettingsAsync();
                break;
            case CheckFix.TurnOnBluetooth:
                // The only place DualAudio changes the radio: the user clicked "Turn on Bluetooth".
                RadioMessage = null;
                var result = await RunSafeAsync(() => _radioControl.TurnOnAsync(_lifetime.Token), RadioTurnOnResult.Failed, "turn on Bluetooth");
                var message = OnboardingText.RadioResult(result);
                RadioMessage = string.IsNullOrEmpty(message) ? null : message;
                CheckTask = RunChecksAsync();
                await CheckTask;
                break;
        }
    }

    [RelayCommand]
    private Task OpenBluetoothSettingsAsync() => RunSafeAsync(_launcher.OpenBluetoothSettingsAsync, "open Bluetooth settings");

    private async Task RunChecksAsync()
    {
        var version = Interlocked.Increment(ref _checkVersion);
        IsChecking = true;
        var result = await RunSafeAsync<CapabilityReport?>(
            async () => await _capabilities.CheckAsync(_lifetime.Token).ConfigureAwait(false),
            null,
            "capability check");

        // Applied through the dispatcher, like every other service result: the stale check and the list rebuild then
        // run as one step on the UI thread, so an older run that finishes late can't interleave with a newer one.
        _dispatcher.Post(() => ApplyCheckResult(version, result));
    }

    /// <summary>UI thread. Drops results of a check that a newer check has superseded.</summary>
    private void ApplyCheckResult(int version, CapabilityReport? result)
    {
        if (_disposed || version != Volatile.Read(ref _checkVersion))
        {
            return;
        }

        IsChecking = false;
        if (result is null)
        {
            return;
        }

        Report = result;
        Checks.Clear();
        foreach (var row in CheckRow.From(result))
        {
            Checks.Add(row);
        }
    }

    // ---------------------------------------------------------------- step 2

    private async Task LoadPcNameAsync()
    {
        var name = await RunSafeAsync(() => _capabilities.GetPcBluetoothNameAsync(_lifetime.Token), Environment.MachineName, "read PC Bluetooth name");
        if (!_disposed && !string.IsNullOrWhiteSpace(name))
        {
            PcBluetoothName = name;
        }
    }

    // ---------------------------------------------------------------- step 4

    [RelayCommand(CanExecute = nameof(CanTryAgain))]
    private Task TryAgainAsync() => ConnectTask = StartConnectAsync(fresh: true);

    private bool CanTryAgain() => !IsSucceeded;

    [RelayCommand]
    private Task UnmuteAsync() => RunSafeAsync(() => _phoneAudio.SetMutedAsync(false), "unmute phone");

    /// <param name="fresh">Close the link first, so the phone gets a brand-new connection.</param>
    private async Task StartConnectAsync(bool fresh)
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        var version = ++_connectVersion;
        IsNoAudioHelpVisible = false;
        ConnectError = null;
        ConnectStatus = $"Connecting to {device.Name}…";
        _logger.LogInformation("Onboarding: connecting to {Device}", LogRedaction.DeviceId(device.Id));

        if (fresh)
        {
            await RunSafeAsync(() => _coordinator.DisconnectAsync(), "disconnect before retry");
        }

        // A normal user connect: the coordinator runs the BUG-001 priming cycle itself if the session is stale.
        // Not tied to this window's lifetime: closing/skipping onboarding must not abort the connection.
        await RunSafeAsync(() => _coordinator.ConnectAsync(device.Id), "connect");
        _ = ShowHelpLaterAsync(version);
        ApplyConnection();
    }

    private async Task ShowHelpLaterAsync(int version)
    {
        try
        {
            await _clock.Delay(NoAudioHelpDelay, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!_disposed && version == _connectVersion && Step == OnboardingStep.Connect && !IsSucceeded)
            {
                _logger.LogInformation("Onboarding: no phone audio after {Delay} s; showing help", NoAudioHelpDelay.TotalSeconds);
                IsNoAudioHelpVisible = true;
            }
        });
    }

    /// <summary>Re-reads coordinator + phone audio state (UI thread). Succeeds only on IsReceivingAudio for the chosen phone.</summary>
    private void ApplyConnection()
    {
        if (_disposed || Step != OnboardingStep.Connect || IsSucceeded || SelectedDevice is not { } device)
        {
            return;
        }

        var state = _coordinator.CurrentState;
        var isOurs = string.Equals(_coordinator.ActiveDevice?.Id, device.Id, StringComparison.OrdinalIgnoreCase);
        IsMutedHintVisible = isOurs && state == ConnectionState.Connected && _phoneAudio.Muted;

        if (!isOurs)
        {
            return;
        }

        ConnectError = state is ConnectionState.Error or ConnectionState.Disconnected && _coordinator.LastError is { } kind
            ? ErrorMessages.For(kind)
            : null;

        switch (state)
        {
            case ConnectionState.Connected when _phoneAudio.IsReceivingAudio:
                Succeed(device);
                break;
            case ConnectionState.Connected:
                ConnectStatus = OnboardingText.PlayNow;
                break;
            case ConnectionState.Enabling or ConnectionState.Opening or ConnectionState.WaitingForPhone:
                ConnectStatus = $"Connecting to {device.Name}…";
                break;
            case ConnectionState.Disconnected when _coordinator.IsReconnecting:
                ConnectStatus = "Reconnecting…";
                break;
            default:
                ConnectStatus = "Not connected.";
                break;
        }
    }

    private void Succeed(DeviceItem device)
    {
        IsSucceeded = true;
        IsNoAudioHelpVisible = false;
        IsMutedHintVisible = false;
        ConnectError = null;
        ConnectStatus = OnboardingText.Success;
        _logger.LogInformation("Onboarding succeeded: phone audio is arriving from {Device}", LogRedaction.DeviceId(device.Id));

        var nickname = AppSettings.CleanNickname(Nickname);
        SaveTask = RunSafeAsync(
            async () =>
            {
                var current = await _settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                var updated = current.WithNickname(device.Id, nickname) with
                {
                    PreferredDeviceId = device.Id,
                    OnboardingCompleted = true,
                };
                await _settings.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
            },
            "save onboarding result");
    }

    // ---------------------------------------------------------------- events (any thread -> UI)

    private void OnConnectionChanged(object? sender, ConnectionStateChangedEventArgs e) => _dispatcher.Post(ApplyConnection);

    private void OnPhoneAudioChanged(object? sender, PhoneAudioChangedEventArgs e) => _dispatcher.Post(ApplyConnection);

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(() =>
        {
            RefreshDevices();
            if (Step == OnboardingStep.Checks && !_disposed)
            {
                CheckTask = RunChecksAsync();
            }
        });

    private void OnRadioChanged(object? sender, RadioStatusChangedEventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (Step == OnboardingStep.Checks && !_disposed)
            {
                CheckTask = RunChecksAsync();
            }
        });

    private void RefreshDevices()
    {
        if (_disposed)
        {
            return;
        }

        var selectedId = SelectedDevice?.Id;
        var latest = _coordinator.Devices.Select(d => DeviceItem.From(d)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Devices.Clear();
        foreach (var item in latest)
        {
            Devices.Add(item);
        }

        HasDevices = Devices.Count > 0;
        SelectedDevice = Devices.FirstOrDefault(d => string.Equals(d.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                         ?? (Devices.Count == 1 ? Devices[0] : null);
    }

    // ---------------------------------------------------------------- helpers

    private async Task RunSafeAsync(Func<Task> call, string what)
    {
        try
        {
            await Task.Run(call);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding action failed: {What}", what);
        }
    }

    private async Task<T> RunSafeAsync<T>(Func<Task<T>> call, T fallback, string what)
    {
        try
        {
            return await Task.Run(call);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding action failed: {What}", what);
            return fallback;
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualAudio.App.Services;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Hotkeys;
using DualAudio.Core.Settings;
using DualAudio.Core.Startup;
using DualAudio.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.ViewModels;

/// <summary>One shortcut row on the settings page.</summary>
public sealed partial class HotkeyRow(HotkeyAction action, string label) : ObservableObject
{
    public HotkeyAction Action { get; } = action;

    public string Label { get; } = label;

    [ObservableProperty]
    private string keys = string.Empty;

    /// <summary>"Shortcut in use by another app" etc.; null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? message;

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    private bool isRecording;
}

/// <summary>
/// Settings page: auto-connect, start with Windows, the three shortcuts (recorder), nickname of the saved phone, log
/// folder, reset, about. Every change is saved immediately; <see cref="SettingsChanged"/> tells the dashboard to reload.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string InUseMessage = "Shortcut in use by another app";
    public const string DuplicateMessage = "Already used by another DualAudio shortcut";
    public const string InvalidMessage = "Use Ctrl, Alt or Win together with another key";
    /// <summary>Shown on the key button while recording. Short, so the button keeps its width (no layout jump).</summary>
    public const string RecordingText = "Press keys…";
    /// <summary>Shown under "Copy diagnostics" and in the "zip created" message.</summary>
    public const string DiagnosticsPrivacyNote = "Includes your phone's and PC's Bluetooth names. Nothing else personal.";
    public const string StartupFailedMessage = "Couldn't update the Windows start-up entry. See the log for details.";

    private readonly ISettingsStore _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly AutoStartRegistration _autoStart;
    private readonly ConnectionCoordinator _coordinator;
    private readonly PhoneAudioService _phoneAudio;
    private readonly ISystemSettingsLauncher _launcher;
    private readonly IConfirmDialog _confirm;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly IDiagnosticsExporter? _diagnosticsExporter;
    private bool _loading;
    private string? _savedPhoneId;

    public SettingsViewModel(
        ISettingsStore settings,
        HotkeyManager hotkeys,
        AutoStartRegistration autoStart,
        ConnectionCoordinator coordinator,
        PhoneAudioService phoneAudio,
        ISystemSettingsLauncher launcher,
        IConfirmDialog confirm,
        IUiDispatcher dispatcher,
        AppPaths paths,
        ILogger<SettingsViewModel>? logger = null,
        IDiagnosticsExporter? diagnosticsExporter = null)
    {
        _diagnosticsExporter = diagnosticsExporter;
        _settings = settings;
        _hotkeys = hotkeys;
        _autoStart = autoStart;
        _coordinator = coordinator;
        _phoneAudio = phoneAudio;
        _launcher = launcher;
        _confirm = confirm;
        _dispatcher = dispatcher;
        _paths = paths;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        Hotkeys =
        [
            new HotkeyRow(HotkeyAction.ToggleMute, "Mute / unmute phone audio"),
            new HotkeyRow(HotkeyAction.VolumeUp, "Phone volume up 5%"),
            new HotkeyRow(HotkeyAction.VolumeDown, "Phone volume down 5%"),
        ];
    }

    /// <summary>A saved setting changed (nickname, reset, …). Raised on the calling (UI) thread.</summary>
    public event EventHandler? SettingsChanged;

    [ObservableProperty]
    private bool autoConnectOnStart = true;

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private string? startWithWindowsMessage;

    public ObservableCollection<HotkeyRow> Hotkeys { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRecordingCommand))]
    private HotkeyRow? recording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNicknameCommand))]
    private bool hasSavedPhone;

    /// <summary>The saved phone's name as Windows reports it (the nickname never changes it).</summary>
    [ObservableProperty]
    private string savedPhoneName = "No phone saved";

    [ObservableProperty]
    private string nickname = string.Empty;

    public string Version => AppInfo.Version;

    /// <summary>"Copy diagnostics" result: the zip's full path, or why it failed. Null until used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticsMessage))]
    private string? diagnosticsMessage;

    /// <summary>The last zip created, for "Show in folder".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowDiagnosticsZipCommand))]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticsZip))]
    private string? diagnosticsZipPath;

    public bool HasDiagnosticsMessage => DiagnosticsMessage is not null;

    public bool HasDiagnosticsZip => DiagnosticsZipPath is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyDiagnosticsCommand))]
    private bool isCopyingDiagnostics;

    public IReadOnlyList<PackageLicense> Licenses => ThirdPartyNotices.Packages;

    /// <summary>Test hook: the latest settings write started by a toggle.</summary>
    internal Task Save { get; private set; } = Task.CompletedTask;

    /// <summary>Exe path written to the start-up entry. Overridable for tests.</summary>
    internal Func<string?> ExePath { get; set; } = () => Environment.ProcessPath;

    public async Task InitializeAsync()
    {
        _hotkeys.Changed -= OnHotkeysChanged;
        _hotkeys.Changed += OnHotkeysChanged;
        await LoadAsync();
    }

    public void Detach() => _hotkeys.Changed -= OnHotkeysChanged;

    // ---------------------------------------------------------------- toggles

    partial void OnAutoConnectOnStartChanged(bool value)
    {
        if (!_loading)
        {
            Save = UpdateAsync(s => s with { AutoConnectOnStart = value }, "save auto-connect");
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        Save = ApplyStartWithWindowsAsync(value);
    }

    private async Task ApplyStartWithWindowsAsync(bool enabled)
    {
        await UpdateAsync(s => s with { StartWithWindows = enabled }, "save start with Windows");
        var exe = ExePath();
        var result = await Task.Run(() => _autoStart.Sync(enabled, exe));
        StartWithWindowsMessage = result == AutoStartSyncResult.Failed ? StartupFailedMessage : null;
    }

    // ---------------------------------------------------------------- hotkey recorder

    [RelayCommand]
    private void BeginRecording(HotkeyRow? row)
    {
        if (row is null)
        {
            return;
        }

        CancelRecording();
        Recording = row;
        row.IsRecording = true;
        row.Message = null;
        row.Keys = RecordingText;
    }

    [RelayCommand(CanExecute = nameof(IsRecordingAny))]
    private void CancelRecording()
    {
        if (Recording is { } row)
        {
            row.IsRecording = false;
            Recording = null;
            RefreshHotkeys();
        }
    }

    private bool IsRecordingAny() => Recording is not null;

    /// <summary>The window captured a key combination while recording. Keeps the previous binding on any failure.</summary>
    public async Task<HotkeyChangeResult?> CompleteRecordingAsync(HotkeyModifiers modifiers, int virtualKey)
    {
        if (Recording is not { } row)
        {
            return null;
        }

        // Stop taking keys now, but keep the row in its recording look ("Press keys…") until the outcome is known, then
        // switch to the final combination in one step. Clearing IsRecording before the change (as before) showed the
        // prompt in the idle style and let the Changed refresh rewrite the row mid-change: the visible "glitch".
        Recording = null;
        var binding = new HotkeyBinding(row.Action, modifiers, virtualKey);
        HotkeyChangeResult? result = null;
        try
        {
            result = await _hotkeys.ChangeAsync(binding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the {Action} shortcut failed", row.Action);
        }

        var current = _hotkeys.Current;
        ApplyRow(row, current);
        row.Message = result switch
        {
            HotkeyChangeResult.InUseByAnotherApp => InUseMessage,
            HotkeyChangeResult.AlreadyUsedInDualAudio => DuplicateMessage,
            HotkeyChangeResult.Invalid => InvalidMessage,
            _ => row.Message,
        };
        row.IsRecording = false;
        RefreshHotkeys(except: row);
        return result;
    }

    // ---------------------------------------------------------------- nickname, log folder, reset

    [RelayCommand(CanExecute = nameof(HasSavedPhone))]
    private async Task SaveNicknameAsync()
    {
        if (_savedPhoneId is not { } id)
        {
            return;
        }

        var nickname = Nickname;
        await UpdateAsync(s => s.WithNickname(id, nickname), "save nickname");
        Nickname = AppSettings.CleanNickname(nickname) ?? string.Empty;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task OpenLogFolderAsync() => _launcher.OpenFolderAsync(_paths.LogDirectory);

    private bool CanCopyDiagnostics() => _diagnosticsExporter is not null && !IsCopyingDiagnostics;

    /// <summary>Zip on the Desktop: last 3 logs, versions, adapter + driver, outputs, settings (no nicknames), STATE lines.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyDiagnostics))]
    private async Task CopyDiagnosticsAsync()
    {
        if (_diagnosticsExporter is not { } exporter)
        {
            return;
        }

        IsCopyingDiagnostics = true;
        DiagnosticsMessage = "Creating diagnostics…";
        try
        {
            var path = await Task.Run(() => exporter.ExportAsync());
            DiagnosticsZipPath = path;
            DiagnosticsMessage = "Saved to " + path + Environment.NewLine + DiagnosticsPrivacyNote;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating the diagnostics zip failed");
            DiagnosticsZipPath = null;
            DiagnosticsMessage = "Couldn't create the diagnostics file. See the log for details.";
        }
        finally
        {
            IsCopyingDiagnostics = false;
        }
    }

    private bool CanShowDiagnosticsZip() => DiagnosticsZipPath is not null;

    [RelayCommand(CanExecute = nameof(CanShowDiagnosticsZip))]
    private async Task ShowDiagnosticsZipAsync()
    {
        if (DiagnosticsZipPath is not { } path || System.IO.Path.GetDirectoryName(path) is not { } dir)
        {
            return;
        }

        try
        {
            await _launcher.OpenFolderAsync(dir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the diagnostics folder failed");
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (!_confirm.Confirm(
                "Reset settings",
                "Reset all DualAudio settings? This forgets your saved phone and nickname, and resets shortcuts, phone volume " +
                "and start-up options. Your phone stays paired in Windows; the next start shows the setup again."))
        {
            return;
        }

        _logger.LogInformation("User reset all settings");
        try
        {
            await Task.Run(async () =>
            {
                await _settings.SaveAsync(AppSettings.Default, CancellationToken.None).ConfigureAwait(false);
                await _phoneAudio.SetVolumeAsync(AppSettings.Default.PhoneVolume).ConfigureAwait(false);
                await _phoneAudio.SetMutedAsync(AppSettings.Default.PhoneMuted).ConfigureAwait(false);
                _autoStart.Sync(AppSettings.Default.StartWithWindows, ExePath());
            });
            await _hotkeys.ApplyFromSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resetting settings failed");
        }

        await LoadAsync();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- helpers

    private async Task LoadAsync()
    {
        AppSettings settings;
        try
        {
            settings = await Task.Run(() => _settings.LoadAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings for the settings page failed");
            settings = AppSettings.Default;
        }

        _loading = true;
        try
        {
            AutoConnectOnStart = settings.AutoConnectOnStart;
            StartWithWindows = settings.StartWithWindows;
            _savedPhoneId = settings.PreferredDeviceId;
            HasSavedPhone = _savedPhoneId is not null;
            var device = _coordinator.Devices.FirstOrDefault(d => string.Equals(d.Id, _savedPhoneId, StringComparison.OrdinalIgnoreCase));
            SavedPhoneName = _savedPhoneId is null
                ? "No phone saved"
                : string.IsNullOrWhiteSpace(device?.Name) ? "Saved phone (not in range)" : device.Name;
            Nickname = settings.NicknameFor(_savedPhoneId) ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }

        RefreshHotkeys();
    }

    /// <summary>Rows that are recording keep their prompt (a change in progress finishes them itself).</summary>
    private void RefreshHotkeys(HotkeyRow? except = null)
    {
        var current = _hotkeys.Current;
        foreach (var row in Hotkeys)
        {
            if (!row.IsRecording && row != except)
            {
                ApplyRow(row, current);
            }
        }
    }

    private static void ApplyRow(HotkeyRow row, IReadOnlyDictionary<HotkeyAction, HotkeyState> current)
    {
        if (current.TryGetValue(row.Action, out var state))
        {
            row.Keys = HotkeyFormat.Format(state.Binding);
            row.Message = state.IsRegistered ? null : InUseMessage;
        }
        else
        {
            row.Keys = "Not set";
        }
    }

    private void OnHotkeysChanged(object? sender, EventArgs e) => _dispatcher.Post(() => RefreshHotkeys());

    private async Task UpdateAsync(Func<AppSettings, AppSettings> change, string what)
    {
        try
        {
            await Task.Run(async () =>
            {
                var current = await _settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                await _settings.SaveAsync(change(current), CancellationToken.None).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings action failed: {What}", what);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.ViewModels;

/// <summary>What the app does with the windows and the process. Implemented by <c>App</c>.</summary>
public interface IAppShell
{
    /// <summary>Shows and activates the main window (or onboarding, while setup isn't finished).</summary>
    void ShowMainWindow();

    /// <summary>Releases the connection (awaited), then removes the tray icon and ends the process.</summary>
    Task ExitAsync();
}

public enum CloseDecision
{
    /// <summary>Hide the main window; DualAudio keeps running in the tray.</summary>
    HideToTray,

    /// <summary>Hide, and show the one-off "still running in the tray" notification.</summary>
    HideToTrayWithNotice,

    /// <summary>Exit is in progress: let the window close.</summary>
    Close,
}

/// <summary>The page shown inside the main window.</summary>
public enum ShellPage
{
    Dashboard,
    Settings,
}

/// <summary>
/// App-level commands (tray menu, main window), the main window's page (dashboard or settings) and the close-to-tray
/// rule: closing the main window hides it; only Exit ends DualAudio. The first hide shows a notification once
/// (remembered as <c>TrayNoticeShown</c>).
/// </summary>
/// <param name="settingsPageFactory">A fresh <see cref="SettingsViewModel"/> each time the settings page opens.</param>
public sealed partial class ShellViewModel(
    ISettingsStore settings,
    IAppShell shell,
    ILogger<ShellViewModel>? logger = null,
    Func<SettingsViewModel>? settingsPageFactory = null) : ObservableObject
{
    public const string NoticeTitle = "DualAudio is still running";

    /// <summary>Names the tray menu item exactly ("Exit").</summary>
    public const string NoticeMessage = "Phone audio keeps playing. DualAudio is in the tray. Right-click the icon and choose Exit to quit.";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private bool _noticeShown;

    public bool IsExiting { get; private set; }

    /// <summary>Test hook: saving TrayNoticeShown.</summary>
    internal Task NoticeSave { get; private set; } = Task.CompletedTask;

    /// <summary>Test hook: loading the settings page.</summary>
    internal Task SettingsLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage), nameof(IsDashboardPage))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private ShellPage currentPage = ShellPage.Dashboard;

    public bool IsSettingsPage => CurrentPage == ShellPage.Settings;

    public bool IsDashboardPage => CurrentPage == ShellPage.Dashboard;

    /// <summary>The settings page's view model while it is open; null on the dashboard.</summary>
    [ObservableProperty]
    private SettingsViewModel? settingsPage;

    /// <summary>A saved setting changed on the settings page (nickname, reset, …): the dashboard reloads.</summary>
    public event EventHandler? SettingsChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _noticeShown = (await settings.LoadAsync(CancellationToken.None).ConfigureAwait(true)).TrayNoticeShown;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading settings for the shell failed");
        }
    }

    /// <summary>Called from the main window's Closing handler; must decide synchronously.</summary>
    public CloseDecision OnMainWindowClosing()
    {
        if (IsExiting)
        {
            return CloseDecision.Close;
        }

        // Hidden in the tray: the next open starts on the dashboard (and a half-done shortcut recording is cancelled).
        GoBack();

        if (_noticeShown)
        {
            return CloseDecision.HideToTray;
        }

        _noticeShown = true;
        NoticeSave = SaveNoticeShownAsync();
        return CloseDecision.HideToTrayWithNotice;
    }

    /// <summary>"Open DualAudio": the main window on the dashboard.</summary>
    [RelayCommand]
    private void OpenMain()
    {
        GoBack();
        shell.ShowMainWindow();
    }

    /// <summary>Gear button / tray "Settings": the main window on the settings page.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        NavigateToSettings();
        shell.ShowMainWindow();
    }

    /// <summary>Settings page back arrow: returns to the dashboard.</summary>
    [RelayCommand(CanExecute = nameof(IsSettingsPage))]
    private void GoBack()
    {
        if (SettingsPage is { } page)
        {
            page.CancelRecordingCommand.Execute(null);
            page.Detach();
            page.SettingsChanged -= OnSettingsPageChanged;
            SettingsPage = null;
        }

        CurrentPage = ShellPage.Dashboard;
    }

    [RelayCommand]
    private async Task ExitAsync()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        _logger.LogInformation("Exit requested");
        await shell.ExitAsync();
    }

    private void NavigateToSettings()
    {
        if (IsSettingsPage || IsExiting)
        {
            return;
        }

        if (settingsPageFactory is not null)
        {
            var page = settingsPageFactory();
            page.SettingsChanged += OnSettingsPageChanged;
            SettingsPage = page;
            SettingsLoad = page.InitializeAsync();
        }

        CurrentPage = ShellPage.Settings;
    }

    private void OnSettingsPageChanged(object? sender, EventArgs e) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private async Task SaveNoticeShownAsync()
    {
        try
        {
            var current = await settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            await settings.SaveAsync(current with { TrayNoticeShown = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving TrayNoticeShown failed");
        }
    }
}

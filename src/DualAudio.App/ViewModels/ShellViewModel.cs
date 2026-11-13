using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.ViewModels;

/// <summary>What the app does with the windows and the process. Implemented by <c>App</c>.</summary>
public interface IAppShell
{
    void ShowMainWindow();

    void ShowSettings();

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

/// <summary>
/// App-level commands (tray menu, main window) and the close-to-tray rule: closing the main window hides it; only
/// Exit ends DualAudio. The first hide shows a notification once (remembered as <c>TrayNoticeShown</c>).
/// </summary>
public sealed partial class ShellViewModel(ISettingsStore settings, IAppShell shell, ILogger<ShellViewModel>? logger = null) : ObservableObject
{
    public const string NoticeTitle = "DualAudio is still running";
    public const string NoticeMessage = "Phone audio keeps playing. Find DualAudio in the tray; right-click it and choose Exit to quit.";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private bool _noticeShown;

    public bool IsExiting { get; private set; }

    /// <summary>Test hook: saving TrayNoticeShown.</summary>
    internal Task NoticeSave { get; private set; } = Task.CompletedTask;

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

        if (_noticeShown)
        {
            return CloseDecision.HideToTray;
        }

        _noticeShown = true;
        NoticeSave = SaveNoticeShownAsync();
        return CloseDecision.HideToTrayWithNotice;
    }

    [RelayCommand]
    private void OpenMain() => shell.ShowMainWindow();

    [RelayCommand]
    private void OpenSettings() => shell.ShowSettings();

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

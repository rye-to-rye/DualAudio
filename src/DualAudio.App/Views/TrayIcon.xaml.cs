using DualAudio.App.ViewModels;
using H.NotifyIcon.Core;

namespace DualAudio.App.Views;

/// <summary>
/// The notification-area icon. Not part of any window, so <see cref="Show"/> must call ForceCreate. Efficiency Mode is
/// explicitly OFF: it would throttle DualAudio, and hotkeys must respond instantly mid-game.
/// </summary>
public partial class TrayIcon
{
    public TrayIcon(TrayViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    public void Show() => ForceCreate(enablesEfficiencyMode: false);

    /// <summary>A Windows notification; respects Focus Assist / quiet time (never over a fullscreen game).</summary>
    public void ShowNotice(string title, string message) => ShowNotification(title, message, NotificationIcon.Info);
}

namespace DualAudio.App.ViewModels;

/// <summary>DataContext of the tray icon, its flyout and its menu: the dashboard (state, volume, mute, connect) + app commands.</summary>
public sealed class TrayViewModel(DashboardViewModel dashboard, ShellViewModel shell)
{
    public DashboardViewModel Dashboard { get; } = dashboard;

    public ShellViewModel Shell { get; } = shell;
}

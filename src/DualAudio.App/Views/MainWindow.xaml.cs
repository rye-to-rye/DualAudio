using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

/// <summary>
/// The main window. DataContext = the dashboard; <see cref="Shell"/> decides the page (dashboard or settings) and
/// owns the settings page's view model.
/// </summary>
public partial class MainWindow
{
    public MainWindow(DashboardViewModel viewModel, ShellViewModel shell)
    {
        Shell = shell;
        DataContext = viewModel;
        InitializeComponent();

        // The phone level meter only updates while the window is on screen (shown and not minimized).
        void UpdateMeterVisibility() => viewModel.DashboardLevel.SetVisible(IsVisible && WindowState != System.Windows.WindowState.Minimized);
        IsVisibleChanged += (_, _) => UpdateMeterVisibility();
        StateChanged += (_, _) => UpdateMeterVisibility();
    }

    public ShellViewModel Shell { get; }
}

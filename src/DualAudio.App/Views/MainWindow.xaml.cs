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
    }

    public ShellViewModel Shell { get; }
}

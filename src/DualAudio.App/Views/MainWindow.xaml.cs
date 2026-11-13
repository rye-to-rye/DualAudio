using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

public partial class MainWindow
{
    public MainWindow(DashboardViewModel viewModel, ShellViewModel shell)
    {
        DataContext = viewModel;
        InitializeComponent();
        SettingsButton.Command = shell.OpenSettingsCommand;
    }
}

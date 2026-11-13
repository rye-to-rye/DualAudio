using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

public partial class MainWindow
{
    public MainWindow(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

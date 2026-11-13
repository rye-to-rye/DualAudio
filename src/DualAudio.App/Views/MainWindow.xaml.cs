using System.Windows;
using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

public partial class MainWindow
{
    public MainWindow(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Brings the window to the front (second launch of the app).</summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}

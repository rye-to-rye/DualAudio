using System.Windows;

namespace DualAudio.App.Views;

public static class WindowExtensions
{
    /// <summary>Brings the window to the front (second launch of the app).</summary>
    public static void BringToFront(this Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}

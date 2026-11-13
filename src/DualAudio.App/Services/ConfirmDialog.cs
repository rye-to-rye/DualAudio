using System.Windows;

namespace DualAudio.App.Services;

/// <summary>Yes/No confirmation (fake in tests). Only ever shown from a window the user is already using.</summary>
public interface IConfirmDialog
{
    bool Confirm(string title, string message);
}

public sealed class MessageBoxConfirmDialog : IConfirmDialog
{
    public bool Confirm(string title, string message)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}

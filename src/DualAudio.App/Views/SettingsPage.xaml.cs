using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Navigation;
using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

/// <summary>The settings page inside the main window. Lifetime of its view model: <see cref="ShellViewModel"/>.</summary>
public partial class SettingsPage
{
    public SettingsPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>Hotkey recorder: this page's own key events while a row is recording (no global hook).</summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { Recording: not null } viewModel)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            viewModel.CancelRecordingCommand.Execute(null);
            return;
        }

        if (HotkeyKeyMapper.TryMap(e.Key, e.SystemKey, Keyboard.Modifiers, out var modifiers, out var virtualKey))
        {
            await viewModel.CompleteRecordingAsync(modifiers, virtualKey);
        }
    }

    private void OnLicenseLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is best-effort.
        }

        e.Handled = true;
    }
}

using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Navigation;
using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

public partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _viewModel.CancelRecordingCommand.Execute(null);
            _viewModel.Detach();
        };
    }

    /// <summary>Hotkey recorder: this window's own key events while a row is recording (no global hook).</summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Recording is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            _viewModel.CancelRecordingCommand.Execute(null);
            return;
        }

        if (HotkeyKeyMapper.TryMap(e.Key, e.SystemKey, Keyboard.Modifiers, out var modifiers, out var virtualKey))
        {
            await _viewModel.CompleteRecordingAsync(modifiers, virtualKey);
        }
    }

    /// <summary>Header "back" arrow: the settings window sits on top of the dashboard, so closing it goes back.</summary>
    private void OnBackClicked(object sender, System.Windows.RoutedEventArgs e) => Close();

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

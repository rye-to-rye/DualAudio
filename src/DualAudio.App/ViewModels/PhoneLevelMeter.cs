using CommunityToolkit.Mvvm.ComponentModel;

namespace DualAudio.App.ViewModels;

/// <summary>
/// The phone level meter of one window (dashboard or tray flyout). <see cref="Level"/> changes only while the view
/// reports the window visible, so a hidden window costs no bindings or layout. UI thread only.
/// </summary>
public sealed partial class PhoneLevelMeter(Func<double> readLevel) : ObservableObject
{
    /// <summary>Phone peak level, 0..1 (the LevelMeter style's range). 0 = all bars empty.</summary>
    [ObservableProperty]
    private double level;

    public bool IsVisible { get; private set; }

    /// <summary>Called by the view when its window is shown or hidden. Showing jumps straight to the current level.</summary>
    public void SetVisible(bool visible)
    {
        IsVisible = visible;
        Refresh();
    }

    /// <summary>Re-reads the level if the window is visible; otherwise does nothing.</summary>
    internal void Refresh()
    {
        if (IsVisible)
        {
            Level = readLevel();
        }
    }
}

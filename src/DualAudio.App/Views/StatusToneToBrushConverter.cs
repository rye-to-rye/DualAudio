using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

/// <summary>
/// <see cref="StatusTone"/> to the Good / Warn / Bad brush (set from Tokens.xaml in Controls.xaml as <c>StatusToneBrush</c>).
/// Used for every status dot and status text; the text itself always stays, so colour is never the only signal.
/// </summary>
public sealed class StatusToneToBrushConverter : IValueConverter
{
    public Brush? Good { get; set; }

    public Brush? Warn { get; set; }

    public Brush? Bad { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusTone.Good => Good,
        StatusTone.Warn => Warn,
        StatusTone.Bad => Bad,
        _ => DependencyProperty.UnsetValue,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

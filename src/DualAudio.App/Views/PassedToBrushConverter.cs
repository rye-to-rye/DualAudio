using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DualAudio.App.Views;

/// <summary>Capability check row dot: green when passed, red otherwise.</summary>
public sealed class PassedToBrushConverter : IValueConverter
{
    private static readonly Brush Passed = Freeze(Color.FromRgb(0x1E, 0x7A, 0x45));
    private static readonly Brush Failed = Freeze(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Passed : Failed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

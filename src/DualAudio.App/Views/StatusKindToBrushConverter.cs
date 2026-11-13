using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

/// <summary>Status pill colour.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    private static readonly Brush Disconnected = Freeze(Color.FromRgb(0x5A, 0x5A, 0x5A));
    private static readonly Brush Connecting = Freeze(Color.FromRgb(0x9A, 0x6A, 0x00));
    private static readonly Brush Connected = Freeze(Color.FromRgb(0x1F, 0x5F, 0xB0));
    private static readonly Brush Receiving = Freeze(Color.FromRgb(0x1E, 0x7A, 0x45));
    private static readonly Brush Error = Freeze(Color.FromRgb(0xA4, 0x26, 0x2C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusKind.Connecting => Connecting,
        StatusKind.Connected => Connected,
        StatusKind.Receiving => Receiving,
        StatusKind.Error => Error,
        _ => Disconnected,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

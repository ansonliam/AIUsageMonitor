using System.Globalization;
using System.Windows.Data;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace AIUsageMonitor.Converters;

public sealed class HexColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && System.Windows.Media.ColorConverter.ConvertFromString(text) is MediaColor color)
        {
            var brush = new MediaSolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        return MediaBrushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

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
        // The ElementName binding is evaluated before its TextBox receives its bound value.
        // Do not pass that initial empty (or a user's partial) value to WPF's colour parser:
        // it throws a first-chance FormatException even though this preview deliberately
        // treats invalid input as transparent.
        if (value is not string text || !IsHexColor(text))
        {
            return MediaBrushes.Transparent;
        }

        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(text) is MediaColor color)
            {
                var brush = new MediaSolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // While a user is editing the textbox, an incomplete HEX value is expected.
        }
        catch (NotSupportedException)
        {
            // Keep the settings window usable if WPF rejects an unexpected colour format.
        }

        return MediaBrushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool IsHexColor(string text) =>
        text.Length is 4 or 5 or 7 or 9 &&
        text[0] == '#' &&
        text.AsSpan(1).ToString().All(Uri.IsHexDigit);
}

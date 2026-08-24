using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace AIUsageMonitor.Converters;

public sealed class HookStatusColorConverter : IValueConverter
{
    private static readonly MediaBrush Installed = CreateBrush("#FF198754");
    private static readonly MediaBrush NotConfigured = CreateBrush("#FFC27C00");
    private static readonly MediaBrush Invalid = CreateBrush("#FFD32F2F");
    private static readonly MediaBrush Neutral = CreateBrush("#FF65717D");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "Installed" => Installed,
            "Not installed" or "Client not detected" => NotConfigured,
            "Invalid configuration" => Invalid,
            _ => Neutral
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static MediaBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        return brush;
    }
}

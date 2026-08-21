using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace AIUsageMonitor.Converters;

public sealed class UsageColorConverter : IValueConverter
{
    private MediaBrush _healthy = CreateBrush("#2ECC71");
    private MediaBrush _moderate = CreateBrush("#9ACD32");
    private MediaBrush _warning = CreateBrush("#FFD21E");
    private MediaBrush _high = CreateBrush("#FF9800");
    private MediaBrush _critical = CreateBrush("#FF4D4F");
    private readonly MediaBrush _unavailable = CreateBrush("#59616B");
    private double _stage1Maximum = 40;
    private double _stage2Maximum = 70;
    private double _stage3Maximum = 85;
    private double _stage4Maximum = 95;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double percent || double.IsNaN(percent))
        {
            return _unavailable;
        }

        if (percent <= _stage1Maximum)
        {
            return _healthy;
        }

        if (percent <= _stage2Maximum)
        {
            return _moderate;
        }

        if (percent <= _stage3Maximum)
        {
            return _warning;
        }

        return percent <= _stage4Maximum ? _high : _critical;
    }

    public bool TryConfigure(
        string healthy,
        string moderate,
        string warning,
        string high,
        string critical,
        double stage1Maximum,
        double stage2Maximum,
        double stage3Maximum,
        double stage4Maximum,
        double stage5Maximum)
    {
        try
        {
            if (!AreValidStageMaximums(
                    stage1Maximum,
                    stage2Maximum,
                    stage3Maximum,
                    stage4Maximum,
                    stage5Maximum))
            {
                return false;
            }

            var healthyBrush = CreateBrush(healthy);
            var moderateBrush = CreateBrush(moderate);
            var warningBrush = CreateBrush(warning);
            var highBrush = CreateBrush(high);
            var criticalBrush = CreateBrush(critical);
            _healthy = healthyBrush;
            _moderate = moderateBrush;
            _warning = warningBrush;
            _high = highBrush;
            _critical = criticalBrush;
            _stage1Maximum = stage1Maximum;
            _stage2Maximum = stage2Maximum;
            _stage3Maximum = stage3Maximum;
            _stage4Maximum = stage4Maximum;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool AreValidStageMaximums(params double[] maximums) =>
        maximums.All(double.IsFinite) &&
        maximums[0] >= 0 &&
        maximums[^1] <= 100 &&
        maximums.Zip(maximums.Skip(1), (current, next) => current < next).All(isIncreasing => isIncreasing);

    private static MediaBrush CreateBrush(string color)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(color) is not System.Windows.Media.Color parsed)
        {
            throw new FormatException("Invalid colour value.");
        }

        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        return brush;
    }
}

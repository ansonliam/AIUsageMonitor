using AIUsageMonitor.Converters;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class IconPreviewWindow
{
    public IconPreviewWindow(MainViewModel viewModel, DashboardWidgetSettings dashboardWidgetSettings)
    {
        InitializeComponent();
        DataContext = viewModel;

        if (Resources["UsageColorConverter"] is UsageColorConverter converter)
        {
            converter.TryConfigure(
                dashboardWidgetSettings.GreenColorHex,
                dashboardWidgetSettings.LimeColorHex,
                dashboardWidgetSettings.YellowColorHex,
                dashboardWidgetSettings.OrangeColorHex,
                dashboardWidgetSettings.RedColorHex,
                dashboardWidgetSettings.Stage1MaxPercent,
                dashboardWidgetSettings.Stage2MaxPercent,
                dashboardWidgetSettings.Stage3MaxPercent,
                dashboardWidgetSettings.Stage4MaxPercent,
                dashboardWidgetSettings.Stage5MaxPercent);
        }
    }

    private void ClosePreviewMenuItem_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}

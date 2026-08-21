using AIUsageMonitor.Converters;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class IconPreviewWindow
{
    public IconPreviewWindow(MainViewModel viewModel, MainWindow mainWindow)
    {
        InitializeComponent();
        DataContext = viewModel;

        if (Resources["UsageColorConverter"] is UsageColorConverter converter)
        {
            converter.TryConfigure(
                mainWindow.GreenColorHex,
                mainWindow.LimeColorHex,
                mainWindow.YellowColorHex,
                mainWindow.OrangeColorHex,
                mainWindow.RedColorHex,
                mainWindow.Stage1MaxPercent,
                mainWindow.Stage2MaxPercent,
                mainWindow.Stage3MaxPercent,
                mainWindow.Stage4MaxPercent,
                mainWindow.Stage5MaxPercent);
        }
    }

    private void ClosePreviewMenuItem_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}

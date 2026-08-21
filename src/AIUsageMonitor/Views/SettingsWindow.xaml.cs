using System.Windows;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        viewModel.RefreshStatus();
        viewModel.RefreshWindowState();
        DataContext = viewModel;
    }
}

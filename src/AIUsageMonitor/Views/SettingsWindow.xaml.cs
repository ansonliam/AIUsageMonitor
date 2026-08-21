using System.Windows;
using System.Windows.Input;
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressedKey != Key.D || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt))
        {
            return;
        }

        DeveloperControls.Visibility = Visibility.Visible;
        Title = "AI Usage Monitor Settings — Developer Mode";
        e.Handled = true;
    }
}

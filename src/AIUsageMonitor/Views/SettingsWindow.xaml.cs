using System.Drawing;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using AIUsageMonitor.Models;
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

    private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: WpfTextBox textBox })
        {
            return;
        }

        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = TryParseColor(textBox.Text) ?? Color.White
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            textBox.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
    }

    private void AlignToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarMonitorOption option })
        {
            return;
        }

        option.AlignRight = !option.AlignRight;
    }

    private static Color? TryParseColor(string text)
    {
        try
        {
            return ColorTranslator.FromHtml(text);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

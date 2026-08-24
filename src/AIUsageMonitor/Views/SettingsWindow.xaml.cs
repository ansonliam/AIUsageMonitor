using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class SettingsWindow : Window
{
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private HwndSource? _windowSource;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        viewModel.RefreshStatus();
        viewModel.RefreshWindowState();
        DataContext = viewModel;
        SourceInitialized += SettingsWindow_SourceInitialized;
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private static IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmSysCommand)
        {
            var command = wParam.ToInt64() & 0xFFF0;
            if (command is ScMinimize or ScMaximize)
            {
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressedKey != Key.D || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt))
        {
            return;
        }

        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.DeveloperModeEnabled = !viewModel.DeveloperModeEnabled;
        }

        e.Handled = true;
    }
}

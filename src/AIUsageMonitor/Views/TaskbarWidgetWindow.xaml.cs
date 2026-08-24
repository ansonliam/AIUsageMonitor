using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsageMonitor.Converters;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class TaskbarWidgetWindow : Window
{
    // Defensive-only: normal recovery happens instantly via the WinEvent hooks below (foreground
    // change / taskbar location change). This just catches whatever those don't - e.g. a WinEvent
    // hook that silently failed to install, or a state mismatch neither hook happened to fire for.
    // Deliberately slow: it should almost never need to do anything.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(8);
    private const double PositionToleranceDip = 1.0;

    private readonly TaskbarWidgetViewModel _viewModel;
    private readonly TaskbarWidgetSettingsStore _settingsStore;
    private readonly TaskbarWidgetPositioningService _positioningService;
    private readonly MainWindow _mainWindow;
    private readonly DispatcherTimer _watchdogTimer;
    // Kept alive for the hook's lifetime - SetWinEventHook only stores a native function pointer
    // to the delegate, so letting it be collected would leave the hook calling into freed memory.
    private readonly TaskbarInterop.WinEventDelegate _foregroundChangedHandler;
    private readonly TaskbarInterop.WinEventDelegate _taskbarLocationChangedHandler;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;
    private IntPtr _cachedTaskbarHandle = IntPtr.Zero;
    private uint _taskbarCreatedMessage;
    private HwndSource? _windowSource;

    public bool ShowTaskbarWidget { get; private set; }
    public bool ShowCodexOnTaskbar { get; private set; } = true;
    public bool ShowClaudeOnTaskbar { get; private set; } = true;
    public bool ShowAntigravityOnTaskbar { get; private set; } = true;
    public bool ShowCursorOnTaskbar { get; private set; } = true;

    public TaskbarWidgetWindow(
        TaskbarWidgetViewModel viewModel,
        TaskbarWidgetSettingsStore settingsStore,
        TaskbarWidgetPositioningService positioningService,
        MainWindow mainWindow)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        _positioningService = positioningService;
        _mainWindow = mainWindow;
        ProvidersItemsControl.ItemsSource = _viewModel.Providers;

        var settings = _settingsStore.Load();
        ShowTaskbarWidget = settings.ShowTaskbarWidget;
        ShowCodexOnTaskbar = settings.ShowCodexOnTaskbar;
        ShowClaudeOnTaskbar = settings.ShowClaudeOnTaskbar;
        ShowAntigravityOnTaskbar = settings.ShowAntigravityOnTaskbar;
        ShowCursorOnTaskbar = settings.ShowCursorOnTaskbar;
        ApplyProviderVisibility();
        RefreshUsageColors();

        _foregroundChangedHandler = OnForegroundWindowChanged;
        _taskbarLocationChangedHandler = OnTaskbarLocationChanged;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += (_, _) => RunWatchdog();

        SourceInitialized += TaskbarWidgetWindow_SourceInitialized;
        SizeChanged += (_, _) => Reposition();
    }

    public void SetShowTaskbarWidget(bool isVisible)
    {
        if (ShowTaskbarWidget == isVisible)
        {
            return;
        }

        ShowTaskbarWidget = isVisible;
        ApplyWindowVisibility();
        SaveSettings();
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        switch (provider)
        {
            case ProviderKind.Codex:
                ShowCodexOnTaskbar = isVisible;
                break;
            case ProviderKind.Claude:
                ShowClaudeOnTaskbar = isVisible;
                break;
            case ProviderKind.Antigravity:
                ShowAntigravityOnTaskbar = isVisible;
                break;
            default:
                ShowCursorOnTaskbar = isVisible;
                break;
        }

        _viewModel.SetProviderVisible(provider, isVisible);
        SaveSettings();
    }

    // Re-reads the dashboard's current usage-colour-stage settings (MainWindow already exposes
    // them as public properties) so the taskbar strip matches whatever the user configured in
    // Settings, without needing its own separate colour settings UI.
    public void RefreshUsageColors()
    {
        if (Resources["UsageColorConverter"] is not UsageColorConverter converter)
        {
            return;
        }

        converter.TryConfigure(
            _mainWindow.GreenColorHex,
            _mainWindow.LimeColorHex,
            _mainWindow.YellowColorHex,
            _mainWindow.OrangeColorHex,
            _mainWindow.RedColorHex,
            _mainWindow.Stage1MaxPercent,
            _mainWindow.Stage2MaxPercent,
            _mainWindow.Stage3MaxPercent,
            _mainWindow.Stage4MaxPercent,
            _mainWindow.Stage5MaxPercent);
    }

    private void ApplyProviderVisibility()
    {
        _viewModel.SetProviderVisible(ProviderKind.Codex, ShowCodexOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Claude, ShowClaudeOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Antigravity, ShowAntigravityOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Cursor, ShowCursorOnTaskbar);
    }

    public void ApplyStartupVisibility() => ApplyWindowVisibility();

    private void ApplyWindowVisibility()
    {
        if (ShowTaskbarWidget)
        {
            Show();
            Reposition();
            _watchdogTimer.Start();
        }
        else
        {
            _watchdogTimer.Stop();
            Hide();
        }
    }

    private void TaskbarWidgetWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = TaskbarInterop.GetWindowExStyle(handle).ToInt64();
        var newStyle = currentStyle | TaskbarInterop.WsExToolWindow | TaskbarInterop.WsExNoActivate;
        TaskbarInterop.SetWindowExStyle(handle, new IntPtr(newStyle));

        _taskbarCreatedMessage = TaskbarInterop.RegisterWindowMessage("TaskbarCreated");
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        _cachedTaskbarHandle = TaskbarInterop.FindTaskbar();

        // EVENT_SYSTEM_FOREGROUND: fires the instant any window anywhere becomes the foreground
        // window - exactly what happens when a taskbar button is clicked. This is what keeps
        // HWND_TOPMOST from being lost to Explorer re-asserting its own topmost position, without
        // needing to poll for it.
        _foregroundHook = TaskbarInterop.SetWinEventHook(
            TaskbarInterop.EventSystemForeground,
            TaskbarInterop.EventSystemForeground,
            IntPtr.Zero,
            _foregroundChangedHandler,
            0,
            0,
            TaskbarInterop.WinEventOutOfContext);

        // EVENT_OBJECT_LOCATIONCHANGE fires for every window move/resize system-wide, so the
        // callback must filter down to just the taskbar itself before doing anything.
        _locationHook = TaskbarInterop.SetWinEventHook(
            TaskbarInterop.EventObjectLocationChange,
            TaskbarInterop.EventObjectLocationChange,
            IntPtr.Zero,
            _taskbarLocationChangedHandler,
            0,
            0,
            TaskbarInterop.WinEventOutOfContext | TaskbarInterop.WinEventSkipOwnProcess);
    }

    private void OnForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Kept minimal per the hook contract: just re-claim the topmost slot, no repositioning.
        Dispatcher.BeginInvoke(() =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (ShowTaskbarWidget && handle != IntPtr.Zero)
            {
                TaskbarInterop.ForceTopMost(handle);
            }
        });
    }

    private void OnTaskbarLocationChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd != _cachedTaskbarHandle || idObject != TaskbarInterop.ObjIdWindow)
        {
            return;
        }

        Dispatcher.BeginInvoke(Reposition);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == TaskbarInterop.WmDisplayChange || (uint)message == _taskbarCreatedMessage)
        {
            // Explorer restarting or a display change invalidates whichever Shell_TrayWnd handle
            // was cached for the location-change filter above.
            _cachedTaskbarHandle = TaskbarInterop.FindTaskbar();
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void Reposition()
    {
        if (!ShowTaskbarWidget || !IsLoaded)
        {
            return;
        }

        if (_positioningService.TryComputePosition(this, ActualWidth, ActualHeight, out var left, out var top))
        {
            Left = left;
            Top = top;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            TaskbarInterop.ForceTopMost(handle);
        }
    }

    // Defensive-only recovery - see WatchdogInterval. Only touches the window (and only via
    // Reposition, which is the single place that calls SetWindowPos) when something is actually
    // wrong; otherwise this is just two cheap read-only checks.
    private void RunWatchdog()
    {
        if (!ShowTaskbarWidget || !IsLoaded)
        {
            return;
        }

        var currentTaskbarHandle = TaskbarInterop.FindTaskbar();
        if (currentTaskbarHandle != _cachedTaskbarHandle)
        {
            _cachedTaskbarHandle = currentTaskbarHandle;
            Reposition();
            return;
        }

        if (!IsVisible)
        {
            Show();
            Reposition();
            return;
        }

        if (_positioningService.TryComputePosition(this, ActualWidth, ActualHeight, out var expectedLeft, out var expectedTop) &&
            (Math.Abs(Left - expectedLeft) > PositionToleranceDip || Math.Abs(Top - expectedTop) > PositionToleranceDip))
        {
            Reposition();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watchdogTimer.Stop();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;

        if (_foregroundHook != IntPtr.Zero)
        {
            TaskbarInterop.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            TaskbarInterop.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        base.OnClosed(e);
    }

    private void SaveSettings() => _settingsStore.Save(new TaskbarWidgetSettings
    {
        ShowTaskbarWidget = ShowTaskbarWidget,
        ShowCodexOnTaskbar = ShowCodexOnTaskbar,
        ShowClaudeOnTaskbar = ShowClaudeOnTaskbar,
        ShowAntigravityOnTaskbar = ShowAntigravityOnTaskbar,
        ShowCursorOnTaskbar = ShowCursorOnTaskbar
    });
}

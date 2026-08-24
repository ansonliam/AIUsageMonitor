using System.Runtime.InteropServices;
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
    // Backstop that guarantees recovery. It re-asserts topmost unconditionally (a single cheap
    // SetWindowPos) rather than only when position/size look wrong, because being *covered* by
    // the taskbar changes neither our position nor our size - a conditional check can't detect it
    // at all, which would otherwise leave the widget hidden indefinitely.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private const double PositionToleranceDip = 1.0;

    // Explorer re-asserts its own topmost slot as part of taskbar interaction, and can do so a
    // moment AFTER we re-assert ours - whichever SetWindowPos(HWND_TOPMOST) call lands last wins
    // the top of the band. Because the taskbar jumping above us doesn't move our window, Windows
    // sends us no message about it (there is no notification when a sibling jumps above you), so
    // a single re-assert per event silently loses that ordering race. Re-asserting a few times
    // across Explorer's settling window covers it without resorting to continuous polling.
    // Interval is deliberately short: since each check is a conditional no-op when nothing is
    // covering us (see ReassertTopMost), the only real cost of checking more often is two cheap
    // reads - and the payoff is directly shorter visible blink, because the widget stays covered
    // only until the next check notices. 10 ticks keeps ~500ms of coverage for a late settle.
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(50);
    private const int SettleTickCount = 10;

    // Nothing user-driven can reorder windows while there has been no input at all, so the
    // watchdog skips its checks entirely once idle. Much shorter than AutoRefreshOptions' own
    // 5-minute idle notion, which exists for a different purpose (how often to poll provider
    // APIs) - here it only needs to be long enough to outlast a pause between clicks.
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(30);

    private readonly TaskbarWidgetViewModel _viewModel;
    private readonly TaskbarWidgetSettingsStore _settingsStore;
    private readonly TaskbarWidgetPositioningService _positioningService;
    // Only needed to force a redraw of every UsageMetricViewModel's colour after this widget's own
    // (independent) usage-colour-stage settings change - see TrySetUsageColors.
    private readonly MainViewModel _mainViewModel;
    private readonly IApplicationController _applicationController;
    private readonly ISystemIdleTimeProvider _idleTimeProvider;
    private readonly DispatcherTimer _watchdogTimer;
    private readonly DispatcherTimer _settleTimer;
    private int _settleTicksRemaining;
    private bool _wasIdle;
    // Kept alive for the hook's lifetime - SetWinEventHook only stores a native function pointer
    // to the delegate, so letting it be collected would leave the hook calling into freed memory.
    private readonly TaskbarInterop.WinEventDelegate _foregroundChangedHandler;
    private readonly TaskbarInterop.WinEventDelegate _taskbarLocationChangedHandler;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;
    private IntPtr _cachedTaskbarHandle = IntPtr.Zero;
    // The tray icon cluster (TrayNotifyWnd) can resize independently of the taskbar's own outer
    // bounds - e.g. reorganizing which icons are hidden/shown changes its width without Shell_
    // TrayWnd's rect changing at all, so it needs its own cached handle to watch.
    private IntPtr _cachedTrayNotifyHandle = IntPtr.Zero;
    private uint _taskbarCreatedMessage;
    private HwndSource? _windowSource;

    // Same purpose as MainWindow.WidgetStateChanged: Settings is non-modal, and this widget's own
    // context menu can change state (Hide) while it is open.
    public event Action? StateChanged;

    public bool ShowTaskbarWidget { get; private set; }
    public bool ShowCodexOnTaskbar { get; private set; } = true;
    public bool ShowClaudeOnTaskbar { get; private set; } = true;
    public bool ShowAntigravityOnTaskbar { get; private set; } = true;
    public bool ShowCursorOnTaskbar { get; private set; } = true;
    public double TaskbarFontSize { get; private set; } = 12;
    public string GreenColorHex { get; private set; } = "#2ECC71";
    public string LimeColorHex { get; private set; } = "#9ACD32";
    public string YellowColorHex { get; private set; } = "#FFD21E";
    public string OrangeColorHex { get; private set; } = "#FF9800";
    public string RedColorHex { get; private set; } = "#FF4D4F";
    public double Stage1MaxPercent { get; private set; } = 40;
    public double Stage2MaxPercent { get; private set; } = 70;
    public double Stage3MaxPercent { get; private set; } = 85;
    public double Stage4MaxPercent { get; private set; } = 95;
    public double Stage5MaxPercent { get; private set; } = 100;

    public TaskbarWidgetWindow(
        TaskbarWidgetViewModel viewModel,
        TaskbarWidgetSettingsStore settingsStore,
        TaskbarWidgetPositioningService positioningService,
        MainViewModel mainViewModel,
        IApplicationController applicationController,
        ISystemIdleTimeProvider idleTimeProvider)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        _positioningService = positioningService;
        _mainViewModel = mainViewModel;
        _applicationController = applicationController;
        _idleTimeProvider = idleTimeProvider;
        ProvidersItemsControl.ItemsSource = _viewModel.Providers;

        var settings = _settingsStore.Load();
        ShowTaskbarWidget = settings.ShowTaskbarWidget;
        ShowCodexOnTaskbar = settings.ShowCodexOnTaskbar;
        ShowClaudeOnTaskbar = settings.ShowClaudeOnTaskbar;
        ShowAntigravityOnTaskbar = settings.ShowAntigravityOnTaskbar;
        ShowCursorOnTaskbar = settings.ShowCursorOnTaskbar;
        TaskbarFontSize = settings.TaskbarFontSize;
        GreenColorHex = settings.GreenColorHex;
        LimeColorHex = settings.LimeColorHex;
        YellowColorHex = settings.YellowColorHex;
        OrangeColorHex = settings.OrangeColorHex;
        RedColorHex = settings.RedColorHex;
        Stage1MaxPercent = settings.Stage1MaxPercent;
        Stage2MaxPercent = settings.Stage2MaxPercent;
        Stage3MaxPercent = settings.Stage3MaxPercent;
        Stage4MaxPercent = settings.Stage4MaxPercent;
        Stage5MaxPercent = settings.Stage5MaxPercent;
        ApplyProviderVisibility();
        ApplySavedUsageColors();
        ApplyFontSize();

        _foregroundChangedHandler = OnForegroundWindowChanged;
        _taskbarLocationChangedHandler = OnTaskbarLocationChanged;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += (_, _) => RunWatchdog();
        _settleTimer = new DispatcherTimer { Interval = SettleInterval };
        _settleTimer.Tick += (_, _) =>
        {
            ReassertTopMost();
            if (--_settleTicksRemaining <= 0)
            {
                _settleTimer.Stop();
            }
        };

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

    public void SetTaskbarFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize))
        {
            return;
        }

        // Floor only, no ceiling - matches every other live-bound numeric field in Settings
        // (MetricLabelWidth, ProgressBarHeight, DashboardWidgetHeight). A ceiling near typical
        // values (e.g. 36) would clamp mid-typing keystrokes back to itself and make it
        // impossible to type past it, and a floor above single digits (e.g. 6) does the same
        // thing typing "1" of "14" - snaps to 6 before the second digit is entered.
        var normalized = Math.Max(1, fontSize);
        if (Math.Abs(TaskbarFontSize - normalized) < 0.01)
        {
            return;
        }

        TaskbarFontSize = normalized;
        ApplyFontSize();
        SaveSettings();
    }

    private void ApplyFontSize() => Resources["TaskbarMetricFontSize"] = TaskbarFontSize;

    // Independent from the dashboard widget's own usage-colour-stage settings - each widget has
    // its own converter instance (see TaskbarWidgetWindow.xaml's Resources), so configuring this
    // one has no effect on the dashboard's and vice versa.
    public bool TrySetUsageColors(
        string green,
        string lime,
        string yellow,
        string orange,
        string red,
        double stage1Maximum,
        double stage2Maximum,
        double stage3Maximum,
        double stage4Maximum,
        double stage5Maximum)
    {
        if (!TryConfigureUsageColors(
                green,
                lime,
                yellow,
                orange,
                red,
                stage1Maximum,
                stage2Maximum,
                stage3Maximum,
                stage4Maximum,
                stage5Maximum))
        {
            return false;
        }

        GreenColorHex = green;
        LimeColorHex = lime;
        YellowColorHex = yellow;
        OrangeColorHex = orange;
        RedColorHex = red;
        Stage1MaxPercent = stage1Maximum;
        Stage2MaxPercent = stage2Maximum;
        Stage3MaxPercent = stage3Maximum;
        Stage4MaxPercent = stage4Maximum;
        Stage5MaxPercent = stage5Maximum;
        // Both widgets bind to the same shared UsageMetricViewModel instances (see
        // TaskbarWidgetViewModel), each through its own converter - forcing a PropertyChanged
        // here is what makes the already-visible values re-run through this widget's converter
        // with its newly configured colours, without waiting for the next real usage update.
        _mainViewModel.RefreshUsageColors();
        SaveSettings();
        return true;
    }

    public void ResetUsageColorsToDefault() =>
        TrySetUsageColors("#2ECC71", "#9ACD32", "#FFD21E", "#FF9800", "#FF4D4F", 40, 70, 85, 95, 100);

    private void ApplySavedUsageColors()
    {
        if (TryConfigureUsageColors(
                GreenColorHex,
                LimeColorHex,
                YellowColorHex,
                OrangeColorHex,
                RedColorHex,
                Stage1MaxPercent,
                Stage2MaxPercent,
                Stage3MaxPercent,
                Stage4MaxPercent,
                Stage5MaxPercent))
        {
            return;
        }

        GreenColorHex = "#2ECC71";
        LimeColorHex = "#9ACD32";
        YellowColorHex = "#FFD21E";
        OrangeColorHex = "#FF9800";
        RedColorHex = "#FF4D4F";
        Stage1MaxPercent = 40;
        Stage2MaxPercent = 70;
        Stage3MaxPercent = 85;
        Stage4MaxPercent = 95;
        Stage5MaxPercent = 100;
        TryConfigureUsageColors(
            GreenColorHex,
            LimeColorHex,
            YellowColorHex,
            OrangeColorHex,
            RedColorHex,
            Stage1MaxPercent,
            Stage2MaxPercent,
            Stage3MaxPercent,
            Stage4MaxPercent,
            Stage5MaxPercent);
    }

    private bool TryConfigureUsageColors(
        string green,
        string lime,
        string yellow,
        string orange,
        string red,
        double stage1Maximum,
        double stage2Maximum,
        double stage3Maximum,
        double stage4Maximum,
        double stage5Maximum) =>
        Resources["UsageColorConverter"] is UsageColorConverter converter &&
        converter.TryConfigure(
            green,
            lime,
            yellow,
            orange,
            red,
            stage1Maximum,
            stage2Maximum,
            stage3Maximum,
            stage4Maximum,
            stage5Maximum);

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
            _settleTimer.Stop();
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

        RefreshCachedTaskbarHandles();

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
        Dispatcher.BeginInvoke(BeginTopMostSettle);
    }

    // Only re-asserts when something is actually covering us. The check matters as much as the
    // fix: this window is layered (AllowsTransparency), so an unconditional SetWindowPos costs a
    // repaint that is itself visible as a flicker - and with the settle sequence below firing
    // several times per event, blind re-asserting turns one brief flicker into several.
    private void ReassertTopMost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!ShowTaskbarWidget || handle == IntPtr.Zero)
        {
            return;
        }

        if (!TaskbarInterop.GetWindowRect(handle, out var rect))
        {
            TaskbarInterop.ForceTopMost(handle);
            return;
        }

        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        if (TaskbarInterop.IsObscuredAt(handle, centerX, centerY))
        {
            TaskbarInterop.ForceTopMost(handle);
        }
    }

    // Re-assert now, then again a few times over Explorer's settling window - see SettleInterval.
    private void BeginTopMostSettle()
    {
        if (!ShowTaskbarWidget)
        {
            return;
        }

        ReassertTopMost();
        _settleTicksRemaining = SettleTickCount;
        _settleTimer.Stop();
        _settleTimer.Start();
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
        // Reorganizing which tray icons are hidden/shown resizes TrayNotifyWnd without Shell_
        // TrayWnd's own outer bounds changing at all, so both handles need watching - only
        // Shell_TrayWnd moving/resizing wouldn't catch that case.
        if (idObject != TaskbarInterop.ObjIdWindow ||
            (hwnd != _cachedTaskbarHandle && hwnd != _cachedTrayNotifyHandle))
        {
            return;
        }

        Dispatcher.BeginInvoke(Reposition);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == TaskbarInterop.WmWindowPosChanging)
        {
            InterceptWindowPosChanging(lParam);
            return IntPtr.Zero;
        }

        if (message == TaskbarInterop.WmDisplayChange || (uint)message == _taskbarCreatedMessage)
        {
            // Explorer restarting or a display change invalidates whichever handles were cached
            // for the location-change filter above.
            RefreshCachedTaskbarHandles();
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    // Rewrites a pending z-order change to this window in place, before Windows applies it.
    // NOTE: this only covers changes to OUR OWN window - it does nothing for the common case of
    // Explorer raising the taskbar above us, which moves the taskbar rather than us and so sends
    // no message here at all. That case is handled by BeginTopMostSettle/RunWatchdog instead.
    private void InterceptWindowPosChanging(IntPtr lParam)
    {
        if (!ShowTaskbarWidget)
        {
            return;
        }

        var pos = Marshal.PtrToStructure<TaskbarInterop.WindowPos>(lParam);
        if (pos.HwndInsertAfter == TaskbarInterop.HwndTopmost &&
            (pos.Flags & TaskbarInterop.SwpNoZOrder) == 0)
        {
            return;
        }

        pos.HwndInsertAfter = TaskbarInterop.HwndTopmost;
        pos.Flags &= ~TaskbarInterop.SwpNoZOrder;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    private void RefreshCachedTaskbarHandles()
    {
        _cachedTaskbarHandle = TaskbarInterop.FindTaskbar();
        _cachedTrayNotifyHandle = TaskbarInterop.FindTrayNotifyArea(_cachedTaskbarHandle);
    }

    private void Reposition()
    {
        if (!ShowTaskbarWidget || !IsLoaded)
        {
            return;
        }

        if (_positioningService.TryComputePosition(this, ActualWidth, out var left, out var top, out var taskbarHeight))
        {
            Left = left;
            Top = top;
            Height = taskbarHeight;
        }

        ReassertTopMost();
    }

    // Recovery backstop - see WatchdogInterval for why the topmost re-assert here is
    // unconditional rather than gated on the position/size checks below.
    private void RunWatchdog()
    {
        if (!ShowTaskbarWidget || !IsLoaded)
        {
            return;
        }

        if (_idleTimeProvider.GetIdleTime() >= IdleThreshold)
        {
            // No input at all, so nothing user-driven can be reordering windows - see
            // IdleThreshold. One cheap read and we're done until input resumes.
            _wasIdle = true;
            return;
        }

        if (_wasIdle)
        {
            _wasIdle = false;
            // Returning from idle is exactly when taskbar geometry and z-order are most likely to
            // have been shuffled while we deliberately weren't looking (lock screen, monitor
            // sleep/wake, resolution or DPI change on resume), so do the full correction rather
            // than a single check.
            RefreshCachedTaskbarHandles();
            Reposition();
            BeginTopMostSettle();
            return;
        }

        ReassertTopMost();

        var currentTaskbarHandle = TaskbarInterop.FindTaskbar();
        var currentTrayNotifyHandle = TaskbarInterop.FindTrayNotifyArea(currentTaskbarHandle);
        if (currentTaskbarHandle != _cachedTaskbarHandle || currentTrayNotifyHandle != _cachedTrayNotifyHandle)
        {
            _cachedTaskbarHandle = currentTaskbarHandle;
            _cachedTrayNotifyHandle = currentTrayNotifyHandle;
            Reposition();
            return;
        }

        if (!IsVisible)
        {
            Show();
            Reposition();
            return;
        }

        if (_positioningService.TryComputePosition(this, ActualWidth, out var expectedLeft, out var expectedTop, out var expectedHeight) &&
            (Math.Abs(Left - expectedLeft) > PositionToleranceDip ||
             Math.Abs(Top - expectedTop) > PositionToleranceDip ||
             Math.Abs(Height - expectedHeight) > PositionToleranceDip))
        {
            Reposition();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watchdogTimer.Stop();
        _settleTimer.Stop();
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

    // The context menu's own popup sits above everything (including the taskbar) while open, so
    // closing it is a known point where this window can end up a slot lower in the topmost band.
    // Worth handling explicitly rather than waiting for whatever gets clicked next to fire
    // EVENT_SYSTEM_FOREGROUND.
    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => BeginTopMostSettle();

    // Mirrors TrayIconService's menu exactly - same items, same underlying actions.
    private void OpenMenuItem_Click(object sender, RoutedEventArgs e) => _applicationController.ShowMainWindow();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _applicationController.ShowSettings();

    private async void RefreshAllMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _applicationController.RefreshAllAsync();

    private void HideMenuItem_Click(object sender, RoutedEventArgs e) => SetShowTaskbarWidget(false);

    private void SaveSettings()
    {
        _settingsStore.Save(new TaskbarWidgetSettings
        {
            ShowTaskbarWidget = ShowTaskbarWidget,
            ShowCodexOnTaskbar = ShowCodexOnTaskbar,
            ShowClaudeOnTaskbar = ShowClaudeOnTaskbar,
            ShowAntigravityOnTaskbar = ShowAntigravityOnTaskbar,
            ShowCursorOnTaskbar = ShowCursorOnTaskbar,
            TaskbarFontSize = TaskbarFontSize,
            GreenColorHex = GreenColorHex,
            LimeColorHex = LimeColorHex,
            YellowColorHex = YellowColorHex,
            OrangeColorHex = OrangeColorHex,
            RedColorHex = RedColorHex,
            Stage1MaxPercent = Stage1MaxPercent,
            Stage2MaxPercent = Stage2MaxPercent,
            Stage3MaxPercent = Stage3MaxPercent,
            Stage4MaxPercent = Stage4MaxPercent,
            Stage5MaxPercent = Stage5MaxPercent
        });

        // Every state setter routes through here - see StateChanged.
        StateChanged?.Invoke();
    }
}
